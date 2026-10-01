using FakeItEasy;
using FluentAssertions;
using InTheHand.Bluetooth;
using SousVide;
using SousVide.Unfucked.Bluetooth;
using System.Text;
using Xunit;
using GattCharacteristicValueChangedEventArgs = SousVide.Unfucked.Bluetooth.GattCharacteristicValueChangedEventArgs;

namespace Tests;

public sealed class ConnectionRecoveryTest {
    [Fact]
    public async Task FailedHandshakeReleasesGattConnection() {
        var device = A.Fake<IBluetoothDevice>();
        var server = A.Fake<IRemoteGattServer>();
        A.CallTo(() => device.Id).Returns("test");
        A.CallTo(() => device.Gatt).Returns(server);
        A.CallTo(() => server.GetPrimaryServicesAsync(A<BluetoothUuid>._)).Returns([]);
        Func<Task> connect = () => AnovaPrecisionCooker.CreateConnected(device);
        await connect.Should().ThrowAsync<Exception>();
        A.CallTo(() => server.Disconnect()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task NotificationFailureAlsoReleasesGattConnection() {
        var device = A.Fake<IBluetoothDevice>();
        var server = A.Fake<IRemoteGattServer>();
        var service = A.Fake<IGattService>();
        var characteristic = A.Fake<IGattCharacteristic>();
        A.CallTo(() => device.Gatt).Returns(server);
        A.CallTo(() => server.GetPrimaryServicesAsync(A<BluetoothUuid>._)).Returns([service]);
        A.CallTo(() => service.GetCharacteristicAsync(A<BluetoothUuid>._)).Returns(characteristic);
        A.CallTo(() => characteristic.StartNotificationsAsync()).ThrowsAsync(new IOException("failed"));
        Func<Task> connect = () => AnovaPrecisionCooker.CreateConnected(device);
        await connect.Should().ThrowAsync<IOException>();
        A.CallTo(() => characteristic.StopNotificationsAsync()).MustHaveHappenedOnceExactly();
        A.CallTo(() => server.Disconnect()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SlowPollsDoNotAccumulateAndDisposalIsIdempotent() {
        var device = A.Fake<IBluetoothDevice>();
        var server = A.Fake<IRemoteGattServer>();
        var service = A.Fake<IGattService>();
        var characteristic = A.Fake<IGattCharacteristic>();
        A.CallTo(() => device.Gatt).Returns(server);
        A.CallTo(() => server.GetPrimaryServicesAsync(A<BluetoothUuid>._)).Returns([service]);
        A.CallTo(() => service.GetCharacteristicAsync(A<BluetoothUuid>._)).Returns(characteristic);
        bool block = false;
        int blockedReads = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => characteristic.WriteValueWithoutResponseAsync(A<byte[]>._)).ReturnsLazily((byte[] bytes) => {
            string command = Encoding.ASCII.GetString(bytes).Trim();
            if (Volatile.Read(ref block) && command == "read temp") {
                Interlocked.Increment(ref blockedReads);
                entered.TrySetResult();
                return release.Task;
            }
            string response = command switch { "read unit" => "f", "status" => "stopped", _ => "135.0" };
            characteristic.CharacteristicValueChanged += Raise.With(characteristic,
                new GattCharacteristicValueChangedEventArgs(Encoding.ASCII.GetBytes(response + "\r")));
            return Task.CompletedTask;
        });
        var cooker = await AnovaPrecisionCooker.CreateConnected(device);
        try {
            Volatile.Write(ref block, true);
            cooker.PropertyUpdateInterval = TimeSpan.FromMilliseconds(10);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            Volatile.Read(ref blockedReads).Should().Be(1);
            await cooker.DisposeAsync();
            cooker.Dispose();
            A.CallTo(() => server.Disconnect()).MustHaveHappenedOnceExactly();
        } finally {
            release.TrySetResult();
            await cooker.DisposeAsync();
        }
    }
}

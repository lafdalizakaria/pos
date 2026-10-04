using System.Net;
using System.Net.Sockets;
using Newrest.Pos.Devices.Badges;
using Newrest.Pos.Devices.Camera;
using Newrest.Pos.Devices.Display;
using Newrest.Pos.Devices.Payment;
using Newrest.Pos.Devices.Printing;

namespace Newrest.Pos.Devices.Tests;

public class EscPosTests
{
    private static readonly ReceiptDocument Receipt = new(
    [
        new ReceiptLine("NEWREST", ReceiptAlignment.Center, Bold: true, Large: true),
        new ReceiptLine("Café crème 7,00"),
    ], Cut: true, OpenDrawer: true);

    [Fact]
    public void Encodes_init_code_page_styles_accents_cut_and_drawer()
    {
        var bytes = new EscPosEncoder().Encode(Receipt);

        bytes.Take(5).Should().Equal(0x1B, (byte)'@', 0x1B, (byte)'t', 16);
        Contains(bytes, [0x1B, (byte)'a', 1]).Should().BeTrue("centered line");
        Contains(bytes, [0x1D, (byte)'!', 0x11]).Should().BeTrue("large line");
        Contains(bytes, [0x43, 0x61, 0x66, 0xE9]).Should().BeTrue("'Café' in Windows-1252");
        Contains(bytes, [0x1D, (byte)'V', 66, 0]).Should().BeTrue("cut");
        bytes.TakeLast(5).Should().Equal(EscPosEncoder.DrawerPulse);
    }

    [Fact]
    public void Unsupported_characters_are_replaced_not_thrown()
    {
        var bytes = new EscPosEncoder().Encode(new ReceiptDocument([new ReceiptLine("مرحبا")], Cut: false));
        bytes.Should().Contain((byte)'?');
        Contains(bytes, [0x1D, (byte)'V']).Should().BeFalse();
    }

    [Fact]
    public async Task Tcp_and_file_transports_deliver_the_bytes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ms = new MemoryStream();
            await client.GetStream().CopyToAsync(ms);
            return ms.ToArray();
        });

        var printer = new EscPosReceiptPrinter(new TcpPrinterTransport("127.0.0.1", port), new EscPosEncoder());
        await printer.PrintAsync(Receipt);
        var received = await accept;
        listener.Stop();
        received.Should().Equal(new EscPosEncoder().Encode(Receipt));
        printer.Status.Should().Be(DeviceStatus.Ready);

        var file = Path.GetTempFileName();
        await new EscPosReceiptPrinter(new FilePrinterTransport(file), new EscPosEncoder()).OpenAsync();
        (await File.ReadAllBytesAsync(file)).Should().Equal(EscPosEncoder.DrawerPulse);
        File.Delete(file);

        var offline = new EscPosReceiptPrinter(new TcpPrinterTransport("127.0.0.1", port, timeoutMs: 500), new EscPosEncoder());
        await FluentActions.Invoking(() => offline.PrintAsync(Receipt)).Should().ThrowAsync<Exception>();
        offline.Status.Should().Be(DeviceStatus.Error);
    }

    [Fact]
    public async Task Simulated_printer_keeps_receipts_and_fails_when_offline()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var printer = new SimulatedReceiptPrinter(folder);
        await printer.PrintAsync(Receipt);
        await printer.OpenAsync();
        printer.Printed.Should().ContainSingle();
        printer.DrawerOpenings.Should().Be(2);
        Directory.GetFiles(folder).Should().ContainSingle();
        printer.Status = DeviceStatus.Offline;
        await FluentActions.Invoking(() => printer.PrintAsync(Receipt)).Should().ThrowAsync<IOException>();
        Directory.Delete(folder, true);
    }

    [Fact]
    public void Layout_helpers()
    {
        ReceiptLayout.TwoColumns("Couscous viande", "42,00", 24).Should().Be("Couscous viande    42,00").And.HaveLength(24);
        ReceiptLayout.TwoColumns("Un libellé beaucoup trop long pour la ligne", "42,00", 20).Should().HaveLength(20).And.EndWith(" 42,00");
        ReceiptLayout.Wrap("Zone franche de Tanger, lot 12, Tanger Maroc", 16).Should().Equal("Zone franche de", "Tanger, lot 12,", "Tanger Maroc");
        ReceiptLayout.Separator(5).Should().Be("-----");
        Receipt.ToPlainText().Should().Contain("NEWREST");
    }

    private static bool Contains(byte[] haystack, byte[] needle) =>
        Enumerable.Range(0, haystack.Length - needle.Length + 1).Any(i => haystack.AsSpan(i, needle.Length).SequenceEqual(needle));
}

public class BadgeReaderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fast_burst_terminated_by_enter_is_a_badge()
    {
        var detector = new KeyboardWedgeDetector();
        string? result = null;
        var t = T0;
        foreach (var c in "BDG-ATL0001\r")
        {
            result = detector.OnCharacter(c, t);
            t = t.AddMilliseconds(8);
        }

        result.Should().Be("BDG-ATL0001");
    }

    [Fact]
    public void Slow_typing_short_input_and_noise_are_ignored()
    {
        var detector = new KeyboardWedgeDetector();
        var t = T0;
        foreach (var c in "1234")
        {
            detector.OnCharacter(c, t);
            t = t.AddMilliseconds(250);
        }

        detector.OnCharacter('\r', t).Should().BeNull("a human typed it");
        detector.OnCharacter('A', t).Should().BeNull();
        detector.OnCharacter('B', t.AddMilliseconds(5)).Should().BeNull();
        detector.OnCharacter('\n', t.AddMilliseconds(10)).Should().BeNull("too short");
        detector.OnCharacter('\t', t).Should().BeNull();
        foreach (var c in new string('9', 70))
        {
            detector.OnCharacter(c, t);
        }

        detector.Reset();
        detector.OnCharacter('\r', t).Should().BeNull();
    }

    [Fact]
    public void Keyboard_wedge_reader_raises_event_and_simulator_too()
    {
        var clock = new ManualClock(T0);
        var reader = new KeyboardWedgeBadgeReader(clock);
        string? scanned = null;
        reader.BadgeScanned += (_, e) => scanned = e.Number;
        foreach (var c in "BADGE42")
        {
            reader.OnCharacter(c).Should().BeFalse();
            clock.Advance(TimeSpan.FromMilliseconds(5));
        }

        reader.OnCharacter('\r').Should().BeTrue();
        scanned.Should().Be("BADGE42");

        var simulated = new SimulatedBadgeReader();
        simulated.BadgeScanned += (_, e) => scanned = e.Number;
        simulated.Scan("SIM-1");
        scanned.Should().Be("SIM-1");
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}

public class OtherDeviceTests
{
    [Fact]
    public async Task Terminal_display_and_camera_simulators()
    {
        (await new ManualPaymentTerminal().RequestPaymentAsync(25m, "T1")).Outcome.Should().Be(TerminalOutcome.ManualEntryRequired);
        var approved = await new SimulatedPaymentTerminal().RequestPaymentAsync(25m, "T1");
        approved.Should().Match<TerminalResult>(r => r.Outcome == TerminalOutcome.Approved && r.AuthorizationCode == "SIM000001");
        (await new SimulatedPaymentTerminal(approve: false).RequestPaymentAsync(1m, "T2")).Outcome.Should().Be(TerminalOutcome.Declined);

        var display = new SimulatedCustomerDisplay();
        display.Current.Should().Be(CustomerDisplayState.Welcome);
        await display.ShowAsync(new CustomerDisplayState([new("Couscous", 1, 42m)], 42m, 20m, 22m, null));
        display.Current.Due.Should().Be(22m);

        var image = await new SimulatedCamera().CaptureAsync();
        image.Jpeg.Should().StartWith(new byte[] { 0xFF, 0xD8 });
        var folder = Directory.CreateTempSubdirectory().FullName;
        await File.WriteAllBytesAsync(Path.Combine(folder, "a.jpg"), [0xFF, 0xD8, 1, 0xFF, 0xD9]);
        (await new SimulatedCamera(folder).CaptureAsync()).Jpeg.Should().HaveCount(5);
        Directory.Delete(folder, true);
    }
}

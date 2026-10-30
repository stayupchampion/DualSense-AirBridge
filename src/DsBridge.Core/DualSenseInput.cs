using System.Buffers.Binary;

namespace DsBridge.Core;

[Flags]
public enum DualSenseButtons : uint
{
    None = 0,
    DpadUp = 1 << 0, DpadRight = 1 << 1, DpadDown = 1 << 2, DpadLeft = 1 << 3,
    Square = 1 << 4, Cross = 1 << 5, Circle = 1 << 6, Triangle = 1 << 7,
    L1 = 1 << 8, R1 = 1 << 9, L2 = 1 << 10, R2 = 1 << 11,
    Create = 1 << 12, Options = 1 << 13, L3 = 1 << 14, R3 = 1 << 15,
    PlayStation = 1 << 16, Touchpad = 1 << 17, Microphone = 1 << 18
}

public readonly record struct DualSenseTouchPoint(bool Active, byte Id, ushort X, ushort Y);
public readonly record struct DualSenseRawVector(short X, short Y, short Z);

/// <summary>Raw values from one full USB report. SensorTimestamp belongs to the controller's
/// clock, not the PC clock; it is not a measurement of transport or physical input latency.</summary>
public readonly record struct DualSenseInputSnapshot
{
    public byte LX { get; init; }
    public byte LY { get; init; }
    public byte RX { get; init; }
    public byte RY { get; init; }
    public byte L2 { get; init; }
    public byte R2 { get; init; }
    public DualSenseButtons Buttons { get; init; }
    public byte Hat { get; init; }
    public DualSenseTouchPoint Touch1 { get; init; }
    public DualSenseTouchPoint Touch2 { get; init; }
    public DualSenseRawVector Gyroscope { get; init; }
    public DualSenseRawVector Accelerometer { get; init; }
    public byte Counter { get; init; }
    public uint PacketSequence { get; init; }
    public uint SensorTimestamp { get; init; }
}

[Flags]
public enum DualSenseChangeKinds
{
    None = 0,
    ButtonPressed = 1 << 0, ButtonReleased = 1 << 1,
    LeftStick = 1 << 2, RightStick = 1 << 3,
    LeftTrigger = 1 << 4, RightTrigger = 1 << 5,
    TouchDown = 1 << 6, TouchUp = 1 << 7, TouchMove = 1 << 8,
    // These are the physical touchpad switch, not a synthesized finger-tap gesture.
    TouchpadPressed = 1 << 9, TouchpadReleased = 1 << 10
}

public readonly record struct DualSenseInputChanges(
    DualSenseChangeKinds Kinds, DualSenseButtons Pressed, DualSenseButtons Released);

/// <summary>Thresholds use raw report units: axes 0..255, touch coordinates 0..4095.
/// They define software event detection, not hardware latency. Both comparison modes must
/// use identical options. Sensors/counters never generate user-action events.</summary>
public sealed record DualSenseInputOptions
{
    public int StickDeadzone { get; init; } = 8;
    public int StickThreshold { get; init; } = 4;
    public int TriggerDeadzone { get; init; } = 2;
    public int TriggerThreshold { get; init; } = 4;
    public int TouchMoveThreshold { get; init; } = 4;

    internal void Validate()
    {
        Range(StickDeadzone, 0, 127, nameof(StickDeadzone));
        Range(StickThreshold, 1, 255, nameof(StickThreshold));
        Range(TriggerDeadzone, 0, 254, nameof(TriggerDeadzone));
        Range(TriggerThreshold, 1, 255, nameof(TriggerThreshold));
        Range(TouchMoveThreshold, 1, 4095, nameof(TouchMoveThreshold));
    }
    private static void Range(int value, int min, int max, string name)
    {
        if (value < min || value > max) throw new ArgumentOutOfRangeException(name, value, $"Expected {min}..{max}.");
    }
}

public static class DualSenseInput
{
    public const int UsbReportLength = 64;
    private static readonly DualSenseInputOptions Defaults = new();

    // Layout evidence, pinned versions (all offsets below include the report ID):
    // SDL PS5StatePacket_t and HIDAPI_DriverPS5_HandleStatePacket:
    // https://github.com/libsdl-org/SDL/blob/a45cd584df4dcd6fefb2c54b616158d3663a2883/src/joystick/hidapi/SDL_hidapi_ps5.c
    // Full USB size/ID and bridge payload copy:
    // https://github.com/hurryman2212/vds/blob/ec531599dfdd02e14ce68efd8c0d2dcb439894a5/include/vds/ds5_protocol.h
    // https://github.com/hurryman2212/vds/blob/ec531599dfdd02e14ce68efd8c0d2dcb439894a5/src/vds_protocol.cc
    // Caller must verify Sony VID 054C / ordinary DualSense PID 0CE6 on a USB interface.
    // Bytes alone cannot prove the device identity. Pass the actual read length, not the buffer capacity.
    // Exact length excludes Bluetooth simple ID 01 reports (10 or 78 bytes in SDL), and BT ID 31.
    public static bool TryParse(ReadOnlySpan<byte> report, out DualSenseInputSnapshot snapshot)
    {
        snapshot = default;
        if (report.Length != UsbReportLength || report[0] != 0x01) return false;
        var hat = (byte)(report[8] & 0x0f);
        DualSenseButtons buttons = (DualSenseButtons)(report[8] & 0xf0)
            | (DualSenseButtons)((uint)report[9] << 8)
            | (DualSenseButtons)((uint)(report[10] & 0x07) << 16);
        buttons |= hat switch
        {
            0 => DualSenseButtons.DpadUp,
            1 => DualSenseButtons.DpadUp | DualSenseButtons.DpadRight,
            2 => DualSenseButtons.DpadRight,
            3 => DualSenseButtons.DpadRight | DualSenseButtons.DpadDown,
            4 => DualSenseButtons.DpadDown,
            5 => DualSenseButtons.DpadDown | DualSenseButtons.DpadLeft,
            6 => DualSenseButtons.DpadLeft,
            7 => DualSenseButtons.DpadLeft | DualSenseButtons.DpadUp,
            _ => DualSenseButtons.None
        };
        snapshot = new()
        {
            LX = report[1], LY = report[2], RX = report[3], RY = report[4],
            L2 = report[5], R2 = report[6], Counter = report[7], Hat = hat, Buttons = buttons,
            PacketSequence = BinaryPrimitives.ReadUInt32LittleEndian(report[12..16]),
            Gyroscope = Vector(report[16..22]), Accelerometer = Vector(report[22..28]),
            SensorTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(report[28..32]),
            Touch1 = Touch(report[33..37]), Touch2 = Touch(report[37..41])
        };
        return true;
    }

    /// <summary>Compare against a chosen reference. For continuous capture use DualSenseInputTracker,
    /// which retains independent axis/contact references until each threshold is reached.</summary>
    public static DualSenseInputChanges Compare(in DualSenseInputSnapshot previous,
        in DualSenseInputSnapshot current, DualSenseInputOptions? options = null)
    {
        var limits = options ?? Defaults;
        limits.Validate();
        var pressed = current.Buttons & ~previous.Buttons;
        var released = previous.Buttons & ~current.Buttons;
        var kinds = DualSenseChangeKinds.None;
        if ((pressed & ~DualSenseButtons.Touchpad) != 0) kinds |= DualSenseChangeKinds.ButtonPressed;
        if ((released & ~DualSenseButtons.Touchpad) != 0) kinds |= DualSenseChangeKinds.ButtonReleased;
        if ((pressed & DualSenseButtons.Touchpad) != 0) kinds |= DualSenseChangeKinds.TouchpadPressed;
        if ((released & DualSenseButtons.Touchpad) != 0) kinds |= DualSenseChangeKinds.TouchpadReleased;
        if (StickChanged(previous.LX, previous.LY, current.LX, current.LY, limits)) kinds |= DualSenseChangeKinds.LeftStick;
        if (StickChanged(previous.RX, previous.RY, current.RX, current.RY, limits)) kinds |= DualSenseChangeKinds.RightStick;
        if (TriggerChanged(previous.L2, current.L2, limits) || ((pressed | released) & DualSenseButtons.L2) != 0)
            kinds |= DualSenseChangeKinds.LeftTrigger;
        if (TriggerChanged(previous.R2, current.R2, limits) || ((pressed | released) & DualSenseButtons.R2) != 0)
            kinds |= DualSenseChangeKinds.RightTrigger;
        kinds |= ContactChanges(previous.Touch1, current, limits) | ContactChanges(previous.Touch2, current, limits);
        if (current.Touch1.Active && !FindContact(previous, current.Touch1.Id, out _)) kinds |= DualSenseChangeKinds.TouchDown;
        if (current.Touch2.Active && !FindContact(previous, current.Touch2.Id, out _)) kinds |= DualSenseChangeKinds.TouchDown;
        return new(kinds, pressed, released);
    }

    private static DualSenseRawVector Vector(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadInt16LittleEndian(data), BinaryPrimitives.ReadInt16LittleEndian(data[2..]),
        BinaryPrimitives.ReadInt16LittleEndian(data[4..]));
    private static DualSenseTouchPoint Touch(ReadOnlySpan<byte> data) => new((data[0] & 0x80) == 0,
        (byte)(data[0] & 0x7f), (ushort)(data[1] | ((data[2] & 0x0f) << 8)),
        (ushort)((data[2] >> 4) | (data[3] << 4)));
    private static int Stick(byte value, DualSenseInputOptions limits) => Math.Abs(value - 128) <= limits.StickDeadzone ? 128 : value;
    private static bool StickChanged(byte px, byte py, byte cx, byte cy, DualSenseInputOptions limits) =>
        Math.Max(Math.Abs(Stick(px, limits) - Stick(cx, limits)), Math.Abs(Stick(py, limits) - Stick(cy, limits))) >= limits.StickThreshold;
    private static bool TriggerChanged(byte previous, byte current, DualSenseInputOptions limits) =>
        Math.Abs((previous <= limits.TriggerDeadzone ? 0 : previous) - (current <= limits.TriggerDeadzone ? 0 : current)) >= limits.TriggerThreshold;
    internal static bool TouchMoved(DualSenseTouchPoint previous, DualSenseTouchPoint current, DualSenseInputOptions limits)
    {
        var x = (int)current.X - previous.X;
        var y = (int)current.Y - previous.Y;
        return x * x + y * y >= limits.TouchMoveThreshold * limits.TouchMoveThreshold;
    }
    internal static bool FindContact(in DualSenseInputSnapshot state, byte id, out DualSenseTouchPoint point)
    {
        if (state.Touch1.Active && state.Touch1.Id == id) { point = state.Touch1; return true; }
        if (state.Touch2.Active && state.Touch2.Id == id) { point = state.Touch2; return true; }
        point = default;
        return false;
    }
    private static DualSenseChangeKinds ContactChanges(DualSenseTouchPoint previous, in DualSenseInputSnapshot current, DualSenseInputOptions limits)
    {
        if (!previous.Active) return DualSenseChangeKinds.None;
        if (!FindContact(current, previous.Id, out var point)) return DualSenseChangeKinds.TouchUp;
        return TouchMoved(previous, point, limits) ? DualSenseChangeKinds.TouchMove : DualSenseChangeKinds.None;
    }
}

/// <summary>One tracker per capture/device. Not thread-safe. The first Observe seeds the reference
/// and produces no events. Slow movement accumulates against the last reported position; independent
/// controls do not reset each other's reference. A physical touchpad click is separate from contact.</summary>
public sealed class DualSenseInputTracker
{
    private readonly DualSenseInputOptions options;
    private DualSenseInputSnapshot reference;
    private bool initialized;
    public DualSenseInputTracker(DualSenseInputOptions? options = null)
    {
        this.options = options ?? new();
        this.options.Validate();
    }
    public void Reset() => initialized = false;
    public DualSenseInputChanges Observe(in DualSenseInputSnapshot current)
    {
        if (!initialized) { reference = current; initialized = true; return default; }
        var changes = DualSenseInput.Compare(reference, current, options);
        var kinds = changes.Kinds;
        var next = current with
        {
            LX = kinds.HasFlag(DualSenseChangeKinds.LeftStick) ? current.LX : reference.LX,
            LY = kinds.HasFlag(DualSenseChangeKinds.LeftStick) ? current.LY : reference.LY,
            RX = kinds.HasFlag(DualSenseChangeKinds.RightStick) ? current.RX : reference.RX,
            RY = kinds.HasFlag(DualSenseChangeKinds.RightStick) ? current.RY : reference.RY,
            L2 = kinds.HasFlag(DualSenseChangeKinds.LeftTrigger) ? current.L2 : reference.L2,
            R2 = kinds.HasFlag(DualSenseChangeKinds.RightTrigger) ? current.R2 : reference.R2,
            Touch1 = ContactReference(current.Touch1), Touch2 = ContactReference(current.Touch2)
        };
        reference = next;
        return changes;
    }
    private DualSenseTouchPoint ContactReference(DualSenseTouchPoint current)
    {
        if (!current.Active || !DualSenseInput.FindContact(reference, current.Id, out var previous)) return current;
        return DualSenseInput.TouchMoved(previous, current, options) ? current : previous;
    }
}

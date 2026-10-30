using System.Buffers.Binary;
using DsBridge.Core;

internal static class DualSenseInputTests
{
    internal static void Run(Action<bool, string> check)
    {
        // Synthetic byte fixtures from the pinned SDL USB layout, independent of parser helpers.
        var data = Neutral();
        data[1] = 12; data[2] = 34; data[3] = 56; data[4] = 78;
        data[5] = 90; data[6] = 123; data[7] = 254;
        data[8] = 0xa1; data[9] = 0xa5; data[10] = 7; data[11] = 255;
        data[12] = 0x78; data[13] = 0x56; data[14] = 0x34; data[15] = 0x12;
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(16), short.MinValue);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(18), short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20), -1234);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22), 2345);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(24), -1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 0xfffffffd);
        data[33] = 7; data[34] = 0xbc; data[35] = 0xfa; data[36] = 0xde;
        data[37] = 0xfe; data[38] = 0x80; data[39] = 0x87; data[40] = 0x43;
        check(DualSenseInput.TryParse(data, out var parsed), "parse ordinary DualSense complete USB report");
        check(parsed.LX == 12 && parsed.LY == 34 && parsed.RX == 56 && parsed.RY == 78 && parsed.L2 == 90 && parsed.R2 == 123,
            "USB stick and analog trigger offsets");
        check(parsed.Counter == 254 && parsed.PacketSequence == 0x12345678 && parsed.SensorTimestamp == 0xfffffffd,
            "USB counter and unsigned little-endian device timestamps stay raw");
        check(parsed.Gyroscope == new DualSenseRawVector(short.MinValue, short.MaxValue, -1234)
            && parsed.Accelerometer == new DualSenseRawVector(2345, -1, 0), "signed raw gyro and acceleration offsets");
        check(parsed.Buttons == (DualSenseButtons.DpadUp | DualSenseButtons.DpadRight | DualSenseButtons.Cross | DualSenseButtons.Triangle
            | DualSenseButtons.L1 | DualSenseButtons.L2 | DualSenseButtons.Options | DualSenseButtons.R3
            | DualSenseButtons.PlayStation | DualSenseButtons.Touchpad | DualSenseButtons.Microphone), "normalize face, dpad, shoulder and system buttons");
        check(parsed.Touch1 == new DualSenseTouchPoint(true, 7, 0xabc, 0xdef)
            && parsed.Touch2 == new DualSenseTouchPoint(false, 126, 1920, 1080), "dual touch active bit, ID and packed 12-bit coordinates");
        data[37] &= 0x7f;
        check(DualSenseInput.TryParse(data, out parsed) && parsed.Touch1.Active && parsed.Touch2.Active, "both contacts can be active independently");
        for (int length = 0; length < 64; ++length)
            if (DualSenseInput.TryParse(data.AsSpan(0, length), out _)) throw new Exception("Accepted truncated USB input");
        check(true, "all truncated USB lengths reject without slicing exceptions");
        data[0] = 0x31;
        check(!DualSenseInput.TryParse(data, out parsed) && parsed == default, "BT report ID rejects and clears output snapshot");
        var simpleBluetooth = new byte[78]; simpleBluetooth[0] = 1;
        check(!DualSenseInput.TryParse(simpleBluetooth, out _), "padded Bluetooth simple ID1 report is never parsed as USB full input");
        check(!DualSenseInput.TryParse(new byte[64], out _) && !DualSenseInput.TryParse(new byte[65], out _), "invalid report ID or trailing capacity rejects");

        var neutral = Parse(Neutral());
        var limits = new DualSenseInputOptions { StickDeadzone = 8, StickThreshold = 4, TriggerDeadzone = 2, TriggerThreshold = 4, TouchMoveThreshold = 5 };
        var noisy = neutral with { LX = 135, LY = 121, RX = 130, L2 = 2, SensorTimestamp = 100, Counter = 1,
            Gyroscope = new(1, 2, 3), Touch1 = new(false, 5, 1200, 900) };
        check(DualSenseInput.Compare(neutral, noisy, limits).Kinds == 0, "neutral axis noise, inactive touch coordinates, IMU and clocks are not actions");
        check(DualSenseInput.Compare(neutral, neutral with { LX = 137 }, limits).Kinds == DualSenseChangeKinds.LeftStick,
            "stick leaves configurable center deadzone");
        var offCenter = neutral with { LX = 180 };
        check(DualSenseInput.Compare(offCenter, offCenter with { LX = 183 }, limits).Kinds == 0
            && DualSenseInput.Compare(offCenter, offCenter with { LX = 184 }, limits).Kinds == DualSenseChangeKinds.LeftStick,
            "stick movement threshold boundary away from center");
        check(DualSenseInput.Compare(neutral, neutral with { L2 = 3 }, limits).Kinds == 0
            && DualSenseInput.Compare(neutral, neutral with { L2 = 4 }, limits).Kinds == DualSenseChangeKinds.LeftTrigger,
            "analog trigger threshold is explicit software detection");
        check(DualSenseInput.Compare(neutral with { R2 = 200 }, neutral, limits).Kinds == DualSenseChangeKinds.RightTrigger,
            "trigger return to rest is measured independently");
        var held = neutral with { Buttons = DualSenseButtons.Cross | DualSenseButtons.DpadUp | DualSenseButtons.DpadRight };
        var released = DualSenseInput.Compare(held, neutral);
        check(released.Pressed == 0 && released.Released == held.Buttons && released.Kinds == DualSenseChangeKinds.ButtonReleased,
            "button and diagonal dpad release preserved");
        var opposite = DualSenseInput.Compare(held, neutral with { Buttons = DualSenseButtons.DpadLeft });
        check(opposite.Pressed == DualSenseButtons.DpadLeft && opposite.Released == held.Buttons
            && opposite.Kinds == (DualSenseChangeKinds.ButtonPressed | DualSenseChangeKinds.ButtonReleased), "simultaneous press and release masks");
        var digitalTrigger = DualSenseInput.Compare(neutral, neutral with { Buttons = DualSenseButtons.L2 });
        check(digitalTrigger.Kinds == (DualSenseChangeKinds.ButtonPressed | DualSenseChangeKinds.LeftTrigger),
            "digital trigger edge remains detectable when analog byte is zero");
        var click = neutral with { Buttons = DualSenseButtons.Touchpad };
        check(DualSenseInput.Compare(neutral, click).Kinds == DualSenseChangeKinds.TouchpadPressed
            && DualSenseInput.Compare(click, neutral).Kinds == DualSenseChangeKinds.TouchpadReleased,
            "physical touchpad click edges differ from finger contact and ordinary buttons");

        var touching = neutral with { Touch1 = new(true, 3, 100, 200), Touch2 = new(true, 9, 700, 800) };
        check(DualSenseInput.Compare(neutral, touching, limits).Kinds == DualSenseChangeKinds.TouchDown
            && DualSenseInput.Compare(touching, neutral, limits).Kinds == DualSenseChangeKinds.TouchUp, "finger down and up for either touch slot");
        var swapped = touching with { Touch1 = touching.Touch2, Touch2 = touching.Touch1 };
        check(DualSenseInput.Compare(touching, swapped, limits).Kinds == 0, "contact identity prevents slot reorder becoming false movement");
        check(DualSenseInput.Compare(touching, touching with { Touch2 = new(true, 9, 703, 803) }, limits).Kinds == 0
            && DualSenseInput.Compare(touching, touching with { Touch2 = new(true, 9, 703, 804) }, limits).Kinds == DualSenseChangeKinds.TouchMove,
            "second contact Euclidean move threshold boundary");
        check(DualSenseInput.Compare(touching, touching with { Touch1 = new(true, 4, 100, 200) }, limits).Kinds
            == (DualSenseChangeKinds.TouchDown | DualSenseChangeKinds.TouchUp), "new contact ID is release and press even at same coordinates");

        var trackingLimits = new DualSenseInputOptions { StickDeadzone = 0, StickThreshold = 4, TriggerDeadzone = 0, TriggerThreshold = 4, TouchMoveThreshold = 5 };
        var tracker = new DualSenseInputTracker(trackingLimits);
        check(tracker.Observe(neutral).Kinds == 0, "first observation establishes baseline without a false event");
        for (byte step = 1; step < 4; ++step)
            if (tracker.Observe(neutral with { LX = (byte)(128 + step), L2 = step }).Kinds != 0) throw new Exception("Premature axis threshold");
        check(tracker.Observe(neutral with { LX = 132, L2 = 4 }).Kinds == (DualSenseChangeKinds.LeftStick | DualSenseChangeKinds.LeftTrigger),
            "successive small stick and trigger movement accumulates to threshold");
        tracker.Reset(); tracker.Observe(touching);
        tracker.Observe(touching with { Touch1 = new(true, 3, 101, 200), Buttons = DualSenseButtons.Cross });
        check(tracker.Observe(touching with { Touch1 = new(true, 3, 105, 200), Buttons = DualSenseButtons.Cross }).Kinds == DualSenseChangeKinds.TouchMove,
            "button event does not reset accumulated touch reference");
        tracker.Reset(); tracker.Observe(touching);
        tracker.Observe(touching with { Touch1 = new(true, 9, 700, 800), Touch2 = new(true, 3, 102, 200) });
        check(tracker.Observe(touching with { Touch1 = new(true, 9, 700, 800), Touch2 = new(true, 3, 105, 200) }).Kinds == DualSenseChangeKinds.TouchMove,
            "touch movement accumulates by ID across slot changes");
        tracker.Reset();
        check(tracker.Observe(held).Kinds == 0 && tracker.Observe(neutral).Released == held.Buttons, "reset starts a new independent capture baseline");
        var badLimits = new[] { new DualSenseInputOptions { StickDeadzone = -1 }, new DualSenseInputOptions { StickThreshold = 0 },
            new DualSenseInputOptions { TriggerDeadzone = 255 }, new DualSenseInputOptions { TriggerThreshold = 256 }, new DualSenseInputOptions { TouchMoveThreshold = 4096 } };
        foreach (var bad in badLimits)
        {
            try { DualSenseInput.Compare(neutral, neutral, bad); throw new Exception("Accepted invalid input threshold"); }
            catch (ArgumentOutOfRangeException) { }
        }
        check(true, "invalid threshold options reject instead of making every sample an action");
    }

    private static byte[] Neutral()
    {
        var data = new byte[64]; data[0] = 1;
        data[1] = data[2] = data[3] = data[4] = 128;
        data[8] = 8; data[33] = data[37] = 0x80;
        return data;
    }
    private static DualSenseInputSnapshot Parse(byte[] data) => DualSenseInput.TryParse(data, out var value)
        ? value : throw new Exception("Invalid test input fixture");
}

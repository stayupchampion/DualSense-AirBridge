using System.Buffers.Binary;
namespace DsBridge.Core;

public sealed record ControllerBattery(int? Percent,string State) {
    public string Display=>Percent is int percent?$"电量：约 {percent}% · {State}":$"电量：{State}";
    // Reported capacity is quantized in ten-percent bands, not a fuel-gauge reading.
    // Layout/status reference: SDL_hidapi_ps5.c at a45cd584df4dcd6fefb2c54b616158d3663a2883.
    public static ControllerBattery Decode(int raw) {
        if(raw<0||raw>255)return new(null,"暂不可用");
        var status=raw>>4;var level=raw&15;
        return status switch {
            0=>new(Math.Min(level*10+5,100),"使用电池"),
            1=>new(Math.Min(level*10+5,100),"充电中"),
            2=>new(100,"已充满"),
            _=>new(null,"充电状态未知")
        };
    }
    public static ControllerBattery FromReport(ReadOnlySpan<byte> report,bool bluetooth) {
        if(!bluetooth&&report.Length==64&&report[0]==1)return Decode(report[53]);
        if(bluetooth&&report.Length==78&&report[0]==0x31&&(report[1]&15)==1&&ValidBluetoothCrc(report))
            return Decode(report[54]);
        // Native Bluetooth simple reports do not contain battery data. Never switch
        // the controller's reporting mode just to fill a UI label.
        return new(null,"当前报告未提供电量");
    }
    static bool ValidBluetoothCrc(ReadOnlySpan<byte> report) {
        uint crc=0xffffffff;
        static uint Step(uint value,byte data) {
            value^=data;
            for(var i=0;i<8;i++)value=(value&1)!=0?(value>>1)^0xedb88320:value>>1;
            return value;
        }
        crc=Step(crc,0xa1);
        foreach(var b in report[..^4])crc=Step(crc,b);
        return ~crc==BinaryPrimitives.ReadUInt32LittleEndian(report[^4..]);
    }
}

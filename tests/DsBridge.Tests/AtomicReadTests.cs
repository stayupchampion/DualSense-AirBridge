using System.Text.Json;
using System.Text.Json.Serialization;
using DsBridge.Core;
internal static class AtomicReadTests {
    public static async Task Run(Action<bool,string> check) {
        var file=Path.GetFullPath(Path.Combine(".local","tests","json-sharing-"+Guid.NewGuid().ToString("N"),"state.json"));
        AtomicJson.Write(file,1);
        if(OperatingSystem.IsWindows()) {
            bool blocked=false;
            using(var legacyReader=File.OpenRead(file)) {
                try{AtomicJson.Write(file,2);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){blocked=true;}
            }
            check(blocked,"legacy read sharing can block an atomic status replacement on Windows");
        }
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        GatedValue.Entered=entered;GatedValue.Release=release;
        var read=Task.Run(()=>AtomicJson.Read<GatedValue>(file));
        try {
            check(entered.Wait(TimeSpan.FromSeconds(5)),"snapshot reader holds its stream during deserialization");
            AtomicJson.Write(file,2);
            check(AtomicJson.Read<int>(file)==2,"live read handle permits atomic replacement without exposing partial JSON");
        }finally{release.Set();}
        check((await read.WaitAsync(TimeSpan.FromSeconds(5))).Value==1,"in-flight reader retains the complete prior snapshot");
    }
    [JsonConverter(typeof(GatedConverter))]
    public sealed record GatedValue(int Value) {
        public static ManualResetEventSlim? Entered,Release;
    }
    public sealed class GatedConverter:JsonConverter<GatedValue> {
        public override GatedValue Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options) {
            GatedValue.Entered!.Set();
            if(!GatedValue.Release!.Wait(TimeSpan.FromSeconds(6)))throw new TimeoutException("reader gate timed out");
            return new(reader.GetInt32());
        }
        public override void Write(Utf8JsonWriter writer,GatedValue value,JsonSerializerOptions options)=>writer.WriteNumberValue(value.Value);
    }
}

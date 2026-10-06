using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSO.Core.Evoker.Conversion;

namespace DSO.Core.Evoker.Commands
{
    /// <summary>
    /// JSON ile tarif edilen çağrı - herhangi bir hedefte (EvokerTarget: herhangi bir tip/nesne, PluginTarget: plugin)
    /// AYNI şekilde çalışır. Tek adım ya da sıralı adımlar (<see cref="Steps"/>):
    /// <code>
    /// { "op": "invoke", "member": "Add", "args": [3, 4] }                          // sıralı argüman
    /// { "op": "invoke", "member": "Greet", "args": { "name": "Ali" } }             // isimli argüman (eksik optional = varsayılan)
    /// { "op": "invoke", "member": "Combine", "args": ["a","b"], "argTypes": ["string","string"] }   // overload ipucu
    /// { "op": "get", "member": "BatchSize" }    { "op": "set", "member": "BatchSize", "value": 500 }
    /// { "op": "batch", "member": "Price", "argsList": [[1,10],[2,5]] }               // aynı metot, çok argüman seti
    /// { "steps": [ {...}, {...} ], "stopOnError": true }                             // aynı instance üzerinde sırayla
    /// </code>
    /// Alan adları büyük/küçük harf duyarsız. op verilmezse "invoke".
    /// </summary>
    public sealed class EvokerCommand
    {
        /// <summary>"invoke" (varsayılan), "get", "set", "batch".</summary>
        public string? Op { get; set; }

        /// <summary>Metot / property / field adı (büyük/küçük harf duyarsız da bulunur).</summary>
        public string? Member { get; set; }

        /// <summary>Argümanlar: dizi (sıralı) ya da nesne (parametre adlarıyla). Yoksa argümansız.</summary>
        public JsonElement Args { get; set; }

        /// <summary>Overload ipucu: parametre tipleri ("int", "string", "System.Int64", "Customer"...). Sadece sıralı argümanlarla.</summary>
        public string[]? ArgTypes { get; set; }

        /// <summary>"set" için yazılacak değer.</summary>
        public JsonElement Value { get; set; }

        /// <summary>"batch" için: her elemanı bir argüman seti (dizi ya da nesne).</summary>
        public JsonElement ArgsList { get; set; }

        /// <summary>Adımın etiketi (sonuçta aynen döner) - çok adımlı komutlarda sonuçları ayırt etmek için.</summary>
        public string? As { get; set; }

        /// <summary>Çok adımlı komut. Doluysa Op/Member yok sayılır.</summary>
        public List<EvokerCommand>? Steps { get; set; }

        /// <summary>Çok adımlıda bir adım hata verince dur (varsayılan true). false: kalan adımlar yine çalışır.</summary>
        public bool? StopOnError { get; set; }

        /// <summary>Bu komutun bekleme üst sınırı (ms). Süre dolunca TimeOut hatası döner; kod çalışmaya devam edebilir.</summary>
        public int? TimeoutMs { get; set; }

        /// <summary>
        /// Instance'ı bu komut için oluşturulan hedeflerde (Scoped/Transient ve isimle açılan tipler) constructor argümanları:
        /// dizi ya da nesne (parametre adlarıyla) - metot argümanlarıyla aynı kurallar.
        /// </summary>
        public JsonElement ConstructorArgs { get; set; }

        [JsonIgnore] public bool IsMultiStep => Steps != null;

        // ---------------- oluşturma ----------------

        public static EvokerCommand Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new EvokerCommandException(EvokerErrorCodes.BadRequest, "Komut JSON'u boş.");
            try
            {
                return JsonSerializer.Deserialize<EvokerCommand>(json, ParseOptions)
                    ?? throw new EvokerCommandException(EvokerErrorCodes.BadRequest, "Komut JSON'u boş (null).");
            }
            catch (JsonException ex)
            {
                throw new EvokerCommandException(EvokerErrorCodes.BadRequest, $"Komut JSON'u okunamadı: {ex.Message}");
            }
        }

        public static EvokerCommand FromElement(JsonElement element)
        {
            try
            {
                return element.Deserialize<EvokerCommand>(ParseOptions)
                    ?? throw new EvokerCommandException(EvokerErrorCodes.BadRequest, "Komut JSON'u boş (null).");
            }
            catch (JsonException ex)
            {
                throw new EvokerCommandException(EvokerErrorCodes.BadRequest, $"Komut JSON'u okunamadı: {ex.Message}");
            }
        }

        /// <summary>Kod içinden: <c>EvokerCommand.Invoke("Add", 3, 4)</c>.</summary>
        public static EvokerCommand Invoke(string member, params object?[] args) =>
            new() { Op = "invoke", Member = member, Args = EvokerJson.ToElement(args) };

        /// <summary>Kod içinden isimli argümanlarla: <c>EvokerCommand.InvokeNamed("Greet", new { name = "Ali" })</c>.</summary>
        public static EvokerCommand InvokeNamed(string member, object namedArgs) =>
            new() { Op = "invoke", Member = member, Args = EvokerJson.ToElement(namedArgs) };

        public static EvokerCommand Get(string member) => new() { Op = "get", Member = member };

        public static EvokerCommand Set(string member, object? value) =>
            new() { Op = "set", Member = member, Value = EvokerJson.ToElement(value) };

        public static EvokerCommand Batch(string member, IEnumerable<object?[]> argsList) =>
            new() { Op = "batch", Member = member, ArgsList = EvokerJson.ToElement(argsList.ToList()) };

        public static EvokerCommand Multi(params EvokerCommand[] steps) => new() { Steps = steps.ToList() };

        public string ToJson(bool indented = false) => JsonSerializer.Serialize(this, indented ? WriteIndentedOptions : WriteOptions);

        internal static readonly JsonSerializerOptions ParseOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        // WhenWritingDefault: verilmemiş JsonElement alanları (default = Undefined) ve null'lar yazılmaz - "set"de açıkça
        // verilmiş null değer (ValueKind=Null) ise yazılır; okununca "verilmemiş" ile "null" ayrımı korunur.
        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly JsonSerializerOptions WriteIndentedOptions = new(WriteOptions) { WriteIndented = true };
    }

    /// <summary>Hedefin nesne ömrü - .NET DI ile aynı kavramlar (+ Static).</summary>
    public enum EvokerLifetime
    {
        /// <summary>Tek nesne: ilk ihtiyaçta oluşturulur, sonraki tüm komutlar aynı nesneyi kullanır (durum korunur). Varsayılan.</summary>
        Singleton,
        /// <summary>Komut başına bir nesne: çok adımlı bir komuttaki adımlar aynı nesneyi paylaşır, sonraki komut yenisini alır.</summary>
        Scoped,
        /// <summary>Her adımda yeni nesne (EvokerBuilder'ın varsayılan davranışı).</summary>
        Transient,
        /// <summary>Nesne yok - sadece static üyeler çağrılır (static sınıflar otomatik olarak böyle).</summary>
        Static
    }
}
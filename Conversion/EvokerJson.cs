using System;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace DSO.Core.Evoker.Conversion
{
    /// <summary>
    /// Evoker'ın kullandığı JSON ayarları - tek yerden.
    ///
    ///   - <see cref="Shape"/>: tip ↔ tip ŞEKİL eşlemesi (plugin Point ↔ host PointDto). System.Text.Json VARSAYILANLARI
    ///     (büyük/küçük harf duyarlı, sadece property) - önceki WireValueCodec davranışı birebir korunur.
    ///   - <see cref="Input"/>: dışarıdan gelen JSON'u (komut argümanları, constructor argümanları) parametre tipine
    ///     çevirmek için: property adları büyük/küçük harf DUYARSIZ, enum adı ya da sayısı, sayılar string olarak da
    ///     gelebilir ("12"), field'lar da doldurulur. Web'den gelen veri için hoşgörülü.
    ///   - <see cref="Output"/>: sonuçları JSON'a çevirmek için (Türkçe karakterler kaçışsız, enum'lar metin).
    ///
    /// UNLOAD: System.Text.Json, kullandığı options nesnesinde tip metadata'sını cache'ler - plugin tipleri oraya
    /// girerse plugin'in AssemblyLoadContext'i boşaltılamaz. <see cref="ForgetAssembly"/> options nesnelerini yeniler ve
    /// System.Text.Json'ın global cache'lerini temizler (bkz. ManagedDotNetPluginLoader.UnloadAsync).
    /// </summary>
    public static class EvokerJson
    {
        private static JsonSerializerOptions _shape = CreateShape();
        private static JsonSerializerOptions _input = CreateInput();
        private static JsonSerializerOptions _output = CreateOutput();

        public static JsonSerializerOptions Shape => Volatile.Read(ref _shape);
        public static JsonSerializerOptions Input => Volatile.Read(ref _input);
        public static JsonSerializerOptions Output => Volatile.Read(ref _output);

        private static JsonSerializerOptions CreateShape() => new();

        private static JsonSerializerOptions CreateInput() => new()
        {
            PropertyNameCaseInsensitive = true,
            IncludeFields = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static JsonSerializerOptions CreateOutput() => new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals // NaN/Infinity hata vermesin
        };

        /// <summary>Plugin unload'u için: options nesnelerini yeniler ve STJ'nin global cache'lerini temizler.</summary>
        public static void ForgetAssembly(Assembly assembly)
        {
            Volatile.Write(ref _shape, CreateShape());
            Volatile.Write(ref _input, CreateInput());
            Volatile.Write(ref _output, CreateOutput());
            Commands.EvokerCommandResult.ResetOptions();
            ClearSystemTextJsonGlobalCaches();
        }

        /// <summary>Bir değeri (herhangi bir CLR nesnesi) JsonElement'e çevirir - <see cref="Output"/> ayarlarıyla.</summary>
        public static JsonElement ToElement(object? value)
        {
            if (value is JsonElement je) return je.Clone();
            using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, value?.GetType() ?? typeof(object), Output));
            return doc.RootElement.Clone();
        }

        // .NET 7+ System.Text.Json, aynı ayarlı options nesneleri arasında PAYLAŞILAN global statik cache'ler tutar.
        // Yeni options nesnesi bunları temizlemez; STJ'nin Hot Reload kancası ([MetadataUpdateHandler] tipinin
        // static ClearCache(Type[]?) metodu) çağrılır. Bulunamazsa (farklı runtime sürümü) sessizce geçilir.
        internal static void ClearSystemTextJsonGlobalCaches()
        {
            try
            {
                var stj = typeof(JsonSerializer).Assembly;
                foreach (var attr in stj.GetCustomAttributes<System.Reflection.Metadata.MetadataUpdateHandlerAttribute>())
                {
                    attr.HandlerType
                        .GetMethod("ClearCache", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        ?.Invoke(null, new object?[] { null });
                }
            }
            catch
            {
                // En iyi çaba - unload'u durdurmamalı.
            }
        }
    }
}
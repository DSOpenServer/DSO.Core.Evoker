using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace DSO.Core.Evoker.Conversion
{
    /// <summary>
    /// Değer dönüştürme - Evoker'ın her katmanında (komut çalıştırıcı, plugin in-process/sandbox, IPC) AYNI kurallar.
    /// (Eskiden DSO.Core.Evoker.Plugins.Sandbox.WireValueCodec.ConvertTo idi; WireValueCodec artık buraya yönlendirir.)
    ///
    ///   - <see cref="ConvertTo"/>: bir CLR değerini başka bir tipe: aynı/atanabilir tip, Nullable, enum (ad ya da sayı),
    ///     sayısal genişletme/daraltma, JsonElement → tip, aynı şekilli farklı tipler (derlenmiş eşleyici, olmazsa JSON).
    ///   - <see cref="FromJson"/>: dışarıdan gelen JSON değerini (komut argümanı) parametre tipine - hoşgörülü ayarlarla
    ///     (bkz. EvokerJson.Input). Hedef object ise "doğal" CLR değeri: sayı → int/long/double, metin → string,
    ///     true/false → bool, nesne/dizi → JsonElement.
    ///   - <see cref="JsonScore"/>: overload seçimi için "bu JSON değeri bu parametre tipine uyar mı" puanı.
    /// </summary>
    public static class EvokerValueConverter
    {
        /// <summary>Basit ("yaprak") tip mi: primitive, enum, string, decimal, Guid, tarih/saat, byte[] (Nullable'ları dahil).</summary>
        public static bool IsLeafType(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid)
                || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t == typeof(byte[]);
        }

        public static object? ConvertTo(object? value, Type targetType)
        {
            if (targetType == typeof(object) || targetType == typeof(void)) return value;
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (value == null)
                return targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null
                    ? Activator.CreateInstance(targetType)
                    : null;

            if (targetType.IsInstanceOfType(value) || underlying.IsInstanceOfType(value)) return value;

            if (value is JsonElement je)
                return je.Deserialize(targetType, EvokerJson.Shape);

            if (underlying.IsEnum)
                return value is string es ? Enum.Parse(underlying, es, ignoreCase: true) : Enum.ToObject(underlying, value);

            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(underlying))
                return Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);

            // Aynı şekle sahip farklı tipler: derlenmiş kopyalayıcı (JSON ile aynı sonuç, çok daha hızlı); şekil emin
            // olunamayacak kadar karmaşıksa null döner ve JSON'a düşülür.
            var mapper = CompiledShapeMapper.Get(value.GetType(), targetType);
            if (mapper != null) return mapper(value);

            var json = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), EvokerJson.Shape);
            return JsonSerializer.Deserialize(json, targetType, EvokerJson.Shape);
        }

        /// <summary>Dışarıdan gelen bir JSON değerini <paramref name="targetType"/>'a çevirir (bkz. sınıf açıklaması).</summary>
        public static object? FromJson(JsonElement element, Type targetType)
        {
            if (targetType == typeof(JsonElement)) return element.Clone();
            if (targetType == typeof(JsonElement?)) return element.ValueKind == JsonValueKind.Null ? null : element.Clone();
            if (targetType == typeof(object)) return Natural(element);

            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null ? Activator.CreateInstance(targetType) : null;

            var u = Nullable.GetUnderlyingType(targetType) ?? targetType;
            // char: tek karakterlik metin
            if (u == typeof(char) && element.ValueKind == JsonValueKind.String)
            {
                var s = element.GetString() ?? "";
                if (s.Length != 1) throw new FormatException($"'{s}' tek karakter değil (char bekleniyor).");
                return s[0];
            }
            // Enum: ad (büyük/küçük harf duyarsız) ya da sayı
            if (u.IsEnum)
            {
                if (element.ValueKind == JsonValueKind.String) return Enum.Parse(u, element.GetString()!, ignoreCase: true);
                if (element.ValueKind == JsonValueKind.Number) return Enum.ToObject(u, element.GetInt64());
            }
            return element.Deserialize(targetType, EvokerJson.Input);
        }

        /// <summary>JSON değerinin "doğal" CLR karşılığı (hedef tip object iken).</summary>
        public static object? Natural(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt32(out var i) ? i : e.TryGetInt64(out var l) ? l : e.TryGetDecimal(out var d) ? d : e.GetDouble(),
            _ => e.Clone()
        };

        /// <summary>
        /// Overload seçimi: bu JSON değeri bu parametre tipine ne kadar uyuyor. 0 = uymaz, 1 = genel hedef (object /
        /// JsonElement), 2 = dönüştürülebilir (ör. tam sayı → double), 3 = doğal eşleşme (tam sayı → int, metin → string).
        /// Sadece aday elemek ve sıralamak içindir; dönüşümün başarısını garanti etmez.
        /// </summary>
        public static int JsonScore(JsonElement e, Type t)
        {
            if (t == typeof(object) || t == typeof(JsonElement) || t == typeof(JsonElement?)) return 1;
            var nullableUnder = Nullable.GetUnderlyingType(t);
            var u = nullableUnder ?? t;
            switch (e.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return !t.IsValueType || nullableUnder != null ? 3 : 0;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return u == typeof(bool) ? 3 : 0;
                case JsonValueKind.Number:
                    bool integer = e.TryGetInt64(out _) || e.TryGetUInt64(out _);
                    if (IsIntegral(u)) return integer && FitsIntegral(e, u) ? 3 : 0;
                    if (u == typeof(double) || u == typeof(float) || u == typeof(decimal)) return integer ? 2 : 3;
                    return u.IsEnum && integer ? 2 : 0;
                case JsonValueKind.String:
                    var s = e.GetString() ?? "";
                    if (u == typeof(string)) return 3;
                    if (u == typeof(char)) return s.Length == 1 ? 3 : 0;
                    if (u.IsEnum) return Enum.GetNames(u).Any(n => string.Equals(n, s, StringComparison.OrdinalIgnoreCase)) ? 3 : 0;
                    if (u == typeof(Guid)) return Guid.TryParse(s, out _) ? 3 : 0;
                    if (u == typeof(DateTime) || u == typeof(DateTimeOffset)) return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? 3 : 0;
                    if (u == typeof(TimeSpan)) return TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out _) ? 3 : 0;
                    if (u == typeof(Uri) || u == typeof(byte[]) || u.FullName == "System.DateOnly" || u.FullName == "System.TimeOnly") return 2;
                    if (IsNumeric(u)) return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? 2 : 0; // "12" -> 12
                    return 0;
                case JsonValueKind.Array:
                    return u != typeof(string) && (u.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(u)) ? 3 : 0;
                case JsonValueKind.Object:
                    if (IsLeafType(u) || u.IsArray) return 0;
                    if (typeof(System.Collections.IEnumerable).IsAssignableFrom(u))
                        return u.GetInterfaces().Concat(new[] { u }).Any(i => i.IsGenericType &&
                            (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))) ? 3 : 0;
                    return 3;
                default:
                    return 0;
            }
        }

        // Tam sayı JSON değeri hedef tam sayı tipine sığıyor mu (300 -> byte uymaz; aday elenir).
        private static bool FitsIntegral(JsonElement e, Type u)
        {
            if (e.TryGetInt64(out long l))
            {
                if (u == typeof(long)) return true;
                if (u == typeof(int)) return l >= int.MinValue && l <= int.MaxValue;
                if (u == typeof(short)) return l >= short.MinValue && l <= short.MaxValue;
                if (u == typeof(sbyte)) return l >= sbyte.MinValue && l <= sbyte.MaxValue;
                if (u == typeof(byte)) return l >= byte.MinValue && l <= byte.MaxValue;
                if (u == typeof(ushort)) return l >= ushort.MinValue && l <= ushort.MaxValue;
                if (u == typeof(uint)) return l >= uint.MinValue && l <= uint.MaxValue;
                if (u == typeof(ulong)) return l >= 0;
                return true;
            }
            return u == typeof(ulong) && e.TryGetUInt64(out _);
        }

        /// <summary>
        /// Eşit puanlı overload'lar arasında C#'ın seçimine yakın tercih sırası (küçük = daha iyi): tam sayı JSON
        /// değerinde int → long → uint → ulong → short → ushort → sbyte → byte; ondalıkta double → decimal → float.
        /// Diğer durumlarda 0 (tercih yok).
        /// </summary>
        internal static int NumericPreference(JsonElement e, Type t)
        {
            if (e.ValueKind != JsonValueKind.Number) return 0;
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u == typeof(int) || u == typeof(double)) return 0;
            if (u == typeof(long) || u == typeof(decimal)) return 1;
            if (u == typeof(uint) || u == typeof(float)) return 2;
            if (u == typeof(ulong)) return 3;
            if (u == typeof(short)) return 4;
            if (u == typeof(ushort)) return 5;
            if (u == typeof(sbyte)) return 6;
            if (u == typeof(byte)) return 7;
            return 0;
        }

        private static bool IsIntegral(Type t) =>
            t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte)
            || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort);

        private static bool IsNumeric(Type t) =>
            t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte)
            || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(double) || t == typeof(float)
            || t == typeof(decimal);

        /// <summary>Plugin unload'u için: bu assembly'nin tiplerine dokunan dönüştürücü cache'lerini ve JSON ayarlarını bırakır.</summary>
        public static void ForgetAssembly(Assembly assembly)
        {
            CompiledShapeMapper.ForgetAssembly(assembly, Involves);
            EvokerJson.ForgetAssembly(assembly);
        }

        internal static bool Involves(Type t, Assembly a) =>
            t.Assembly == a
            || (t.HasElementType && Involves(t.GetElementType()!, a))
            || (t.IsGenericType && t.GetGenericArguments().Any(g => Involves(g, a)));
    }
}
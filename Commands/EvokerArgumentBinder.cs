using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DSO.Core.Evoker.Conversion;
using DSO.Core.Evoker.Description;

namespace DSO.Core.Evoker.Commands
{
    /// <summary>
    /// JSON argümanlarını (sıralı dizi ya da isimli nesne) aday metot/constructor'lardan birine bağlar ve parametre
    /// tiplerine çevirir. Kurallar (EvokerBuilder.FindMethod ile aynı ruhta, JSON'a uyarlanmış):
    ///   1) Sıralı: argüman sayısı ≤ parametre sayısı ve kalan parametreler optional olmalı (varsayılanla dolar).
    ///      İsimli: verilen her ad bir parametreye karşılık gelmeli (büyük/küçük harf duyarsız), verilmeyenler optional olmalı.
    ///      İsimli nesne hiçbir adayla eşleşmezse ve TEK parametreli bir aday varsa nesne o parametrenin DEĞERİ sayılır
    ///      (ör. SaveCustomer(Customer c)'ye doğrudan { "Code": "C1" } göndermek).
    ///   2) argTypes verildiyse parametre tipleri onlarla eşleşmeli ("int", "System.Int32", "Int32", "Customer"...).
    ///   3) Adaylar JSON değer türü uyumuna göre puanlanır (tam sayı→int önce double, metin→string/tarih/Guid/enum adı, nesne→sınıf...);
    ///      eşit puanlılarda sayısal tercih (int → long → …, double → decimal → float; değer tipe sığmalı), yine eşitse
    ///      AmbiguousMatch (argTypes ile belirtilmesi istenir). Eksik optional
    ///      sayısı az olan önce gelir.
    ///   4) params / ParamArray: son dizi parametresine argümanlar tek tek de verilebilir ([ "A", "B" ]).
    /// </summary>
    internal static class EvokerArgumentBinder
    {
        public static (T Method, object?[] Args) Bind<T>(string memberName, IReadOnlyList<T> candidates, JsonElement args, string[]? argTypes)
            where T : MethodBase
        {
            if (candidates.Count == 0)
                throw new EvokerCommandException(EvokerErrorCodes.MemberNotFound, $"'{memberName}' bulunamadı.");

            var kind = args.ValueKind;
            if (kind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Array or JsonValueKind.Object))
                throw new EvokerCommandException(EvokerErrorCodes.BadRequest, "\"args\" bir dizi (sıralı) ya da nesne (isimli) olmalı.");

            var callable = candidates.Where(IsCallable).ToList();
            if (callable.Count == 0)
                throw new EvokerCommandException(EvokerErrorCodes.InvalidOperation,
                    $"'{memberName}' çağrılamıyor (ref/out parametre ya da açık generic). İmzalar: {Signatures(candidates)}");

            List<(T M, JsonElement?[] Slots, int Score)> matches;
            if (kind == JsonValueKind.Object)
            {
                if (argTypes != null)
                    throw new EvokerCommandException(EvokerErrorCodes.BadRequest, "\"argTypes\" sadece sıralı (dizi) argümanlarla kullanılabilir.");
                matches = callable.Select(c => MatchNamed(c, args)).Where(x => x.HasValue).Select(x => x!.Value).ToList();
                if (matches.Count == 0)
                {
                    // { ... } tek parametreli metoda nesnenin kendisi olarak
                    matches = callable.Where(c => c.GetParameters().Length >= 1 && AcceptsCount(c, 1))
                        .Select(c => MatchPositional(c, new[] { args }, null)).Where(x => x.HasValue).Select(x => x!.Value)
                        .Where(x => x.Score > 0).ToList();
                }
            }
            else
            {
                var list = kind == JsonValueKind.Array ? args.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
                if (argTypes != null && argTypes.Length != list.Length)
                    throw new EvokerCommandException(EvokerErrorCodes.BadRequest, $"\"argTypes\" ({argTypes.Length}) ile argüman sayısı ({list.Length}) aynı olmalı.");
                matches = callable.Select(c => Better(MatchPositional(c, list, argTypes), MatchParamsExpanded(c, list, argTypes)))
                    .Where(x => x.HasValue).Select(x => x!.Value).ToList();
            }

            if (matches.Count == 0)
                throw new EvokerCommandException(EvokerErrorCodes.InvalidArguments,
                    $"'{memberName}' için verilen argümanlara uyan bir imza yok. Mevcut: {Signatures(callable)}");

            var best = matches.OrderByDescending(m => m.Score).ToList();
            if (best.Count > 1 && best[0].Score == best[1].Score)
            {
                // Eşitlik: sayısal tip tercihi (Math.Max(3,7) -> int, Math.Max(1.5,2.5) -> double)
                var top = best.Where(b => b.Score == best[0].Score)
                    .Select(b => (b.M, b.Slots, b.Score, Pref: Preference(b.M, b.Slots)))
                    .OrderBy(b => b.Pref).ToList();
                if (top[0].Pref < top[1].Pref)
                    return (top[0].M, Convert(memberName, top[0].M, top[0].Slots));
                var tied = top.Where(b => b.Pref == top[0].Pref).Select(b => b.M).ToList();
                throw new EvokerCommandException(EvokerErrorCodes.AmbiguousMatch,
                    $"'{memberName}' için birden fazla imza eşit derecede uyuyor: {Signatures(tied)}. \"argTypes\" ile belirtin (ör. [\"string\",\"int\"]).");
            }

            var chosen = best[0];
            return (chosen.M, Convert(memberName, chosen.M, chosen.Slots));
        }

        private static int Preference(MethodBase m, JsonElement?[] slots)
        {
            var ps = m.GetParameters();
            int p = 0;
            for (int i = 0; i < ps.Length && i < slots.Length; i++)
                if (slots[i] is { } e) p += EvokerValueConverter.NumericPreference(e, ElementType(ps[i]));
            return p;
        }

        private static bool IsCallable(MethodBase m) =>
            !m.ContainsGenericParameters && !m.GetParameters().Any(p => p.ParameterType.IsByRef && !p.IsIn) && !(m is MethodInfo mi && mi.IsAbstract);

        private static bool AcceptsCount(MethodBase m, int n)
        {
            var ps = m.GetParameters();
            if (n > ps.Length) return false;
            for (int i = n; i < ps.Length; i++) if (!ps[i].IsOptional) return false;
            return true;
        }

        private static (T M, JsonElement?[] Slots, int Score)? MatchPositional<T>(T m, JsonElement[] list, string[]? argTypes) where T : MethodBase
        {
            var ps = m.GetParameters();
            if (!AcceptsCount(m, list.Length)) return null;
            int score = 0;
            var slots = new JsonElement?[ps.Length];
            for (int i = 0; i < list.Length; i++)
            {
                var pt = ElementType(ps[i]);
                if (argTypes != null && !TypeNameMatches(argTypes[i], pt, m)) return null;
                int s = EvokerValueConverter.JsonScore(list[i], pt);
                if (s == 0) return null;
                score += s * 10;
                slots[i] = list[i];
            }
            score -= ps.Length - list.Length; // eksik optional ne kadar azsa o kadar iyi
            return (m, slots, score);
        }

        private static (T M, JsonElement?[] Slots, int Score)? Better<T>((T M, JsonElement?[] Slots, int Score)? a, (T M, JsonElement?[] Slots, int Score)? b)
            where T : MethodBase =>
            a == null ? b : b == null ? a : (b.Value.Score > a.Value.Score ? b : a);

        /// <summary>
        /// C# params / VB ParamArray: son parametre dizi ise argümanlar tek tek de verilebilir -
        /// ToplamMiktar("A","B") = ToplamMiktar(new[]{"A","B"}). Hiç verilmezse boş dizi. Normal biçimden bir puan düşük.
        /// </summary>
        private static (T M, JsonElement?[] Slots, int Score)? MatchParamsExpanded<T>(T m, JsonElement[] list, string[]? argTypes) where T : MethodBase
        {
            var ps = m.GetParameters();
            if (ps.Length == 0) return null;
            var last = ps[ps.Length - 1];
            if (!last.ParameterType.IsArray || !last.IsDefined(typeof(ParamArrayAttribute), false)) return null;
            int fixedCount = ps.Length - 1;
            if (list.Length < fixedCount) return null;
            var elementType = last.ParameterType.GetElementType()!;
            int score = 0;
            var slots = new JsonElement?[ps.Length];
            for (int i = 0; i < list.Length; i++)
            {
                var pt = i < fixedCount ? ElementType(ps[i]) : elementType;
                if (argTypes != null && !TypeNameMatches(argTypes[i], pt, m)) return null;
                int s = EvokerValueConverter.JsonScore(list[i], pt);
                if (s == 0) return null;
                score += s * 10;
                if (i < fixedCount) slots[i] = list[i];
            }
            slots[fixedCount] = JsonSerializer.SerializeToElement(list.Skip(fixedCount).ToArray());
            return (m, slots, score - 1);
        }

        private static (T M, JsonElement?[] Slots, int Score)? MatchNamed<T>(T m, JsonElement obj) where T : MethodBase
        {
            var ps = m.GetParameters();
            var slots = new JsonElement?[ps.Length];
            int score = 0, given = 0;
            foreach (var prop in obj.EnumerateObject())
            {
                int idx = Array.FindIndex(ps, p => string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase));
                if (idx < 0 || slots[idx].HasValue) return null;
                int s = EvokerValueConverter.JsonScore(prop.Value, ElementType(ps[idx]));
                if (s == 0) return null;
                score += s * 10;
                slots[idx] = prop.Value;
                given++;
            }
            for (int i = 0; i < ps.Length; i++)
                if (!slots[i].HasValue && !ps[i].IsOptional) return null;
            score -= ps.Length - given;
            return (m, slots, score);
        }

        private static Type ElementType(ParameterInfo p) => p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;

        private static bool TypeNameMatches(string hint, Type t, MethodBase m)
        {
            hint = hint.Trim();
            return string.Equals(hint, EvokerDescriber.Friendly(t, m.DeclaringType?.Assembly), StringComparison.OrdinalIgnoreCase)
                || string.Equals(hint, EvokerDescriber.Friendly(t), StringComparison.OrdinalIgnoreCase)
                || string.Equals(hint, t.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(hint, t.FullName, StringComparison.OrdinalIgnoreCase);
        }

        private static object?[] Convert(string memberName, MethodBase m, JsonElement?[] slots)
        {
            var ps = m.GetParameters();
            var result = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var pt = ElementType(ps[i]);
                if (slots[i].HasValue)
                {
                    try { result[i] = EvokerValueConverter.FromJson(slots[i]!.Value, pt); }
                    catch (Exception ex)
                    {
                        throw new EvokerCommandException(EvokerErrorCodes.InvalidArguments,
                            $"'{memberName}': '{ps[i].Name}' parametresi ({EvokerDescriber.Friendly(pt)}) için değer çevrilemedi: {ex.Message}", ex);
                    }
                }
                else result[i] = DefaultFor(ps[i], pt);
            }
            return result;
        }

        /// <summary>Verilmemiş optional parametrenin değeri: metodun kendi varsayılanı (enum/decimal dahil), yoksa default(T).</summary>
        internal static object? DefaultFor(ParameterInfo p, Type t)
        {
            object? dv = null;
            if (p.HasDefaultValue) { try { dv = p.DefaultValue; } catch { dv = null; } }
            if (dv == null || dv is DBNull || dv == Type.Missing)
                return t.IsValueType && Nullable.GetUnderlyingType(t) == null ? Activator.CreateInstance(t) : null;
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u.IsEnum && !u.IsInstanceOfType(dv)) return Enum.ToObject(u, dv);
            return u.IsInstanceOfType(dv) ? dv : System.Convert.ChangeType(dv, u, System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string Signatures<T>(IEnumerable<T> methods) where T : MethodBase =>
            string.Join("; ", methods.Select(m =>
                $"{(m is ConstructorInfo ? "new " + m.DeclaringType?.Name : m.Name)}({string.Join(", ", m.GetParameters().Select(p => EvokerDescriber.Friendly(ElementType(p)) + " " + p.Name + (p.IsOptional ? " = …" : "")))})"));
    }
}
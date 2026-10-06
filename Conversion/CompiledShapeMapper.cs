using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace DSO.Core.Evoker.Conversion
{
    /// <summary>
    /// Aynı "şekle" sahip iki FARKLI tip arasında (ör. plugin'in Point'i ↔ host'un PointDto'su) derlenmiş kopyalayıcı.
    /// EvokerValueConverter.ConvertTo'nun son çaresi JSON'a serialize edip geri deserialize etmekti (~1.7 µs, ~1 KB/çağrı);
    /// bu sınıf aynı sonucu doğrudan property kopyalamasıyla üretir.
    ///
    /// SONUÇ JSON YOLUYLA BİREBİR AYNI OLMALI - bu yüzden sadece JSON'un davranışının kesin bilindiği basit şekiller
    /// desteklenir, gerisinde null döner ve çağıran JSON'a düşer:
    ///   - hedef: soyut olmayan class/struct, public parametresiz constructor (struct'ta gerekmez), koleksiyon değil
    ///   - eşleme: kaynağın public okunabilir instance property'leri → hedefin public yazılabilir (init değil) property'leri,
    ///     isim BÜYÜK/KÜÇÜK HARF DUYARLI (System.Text.Json varsayılanı); field'lar yok sayılır (IncludeFields=false)
    ///   - eşleşen property tipleri ya AYNI basit tip (EvokerValueConverter.IsLeafType) ya da yine bu kurallara uyan iç içe tipler
    ///   - System.Text.Json attribute'u ([JsonPropertyName], [JsonIgnore], [JsonConverter]...) olan tip/property YOK
    /// Kaynakta olup hedefte olmayan property'ler atlanır, hedefte olup kaynakta olmayanlar varsayılan kalır (JSON gibi).
    /// </summary>
    internal static class CompiledShapeMapper
    {
        // (kaynak, hedef) -> eşleyici; null = bu çift için desteklenmiyor (JSON kullanılır). Plugin tiplerine referans
        // tuttuğu için unload'da ForgetAssembly ile temizlenir.
        private static readonly ConcurrentDictionary<(Type From, Type To), Func<object, object>?> Mappers = new();

        public static Func<object, object>? Get(Type from, Type to) => Mappers.GetOrAdd((from, to), k => TryBuild(k.From, k.To));

        public static void ForgetAssembly(Assembly a, Func<Type, Assembly, bool> involves)
        {
            foreach (var k in Mappers.Keys)
                if (involves(k.From, a) || involves(k.To, a)) Mappers.TryRemove(k, out _);
        }

        private static Func<object, object>? TryBuild(Type from, Type to)
        {
            try
            {
                var src = Expression.Parameter(typeof(object), "src");
                var body = BuildMap(Expression.Convert(src, from), from, to, new HashSet<Type>());
                if (body == null) return null;
                return Expression.Lambda<Func<object, object>>(Expression.Convert(body, typeof(object)), src).Compile();
            }
            catch
            {
                return null; // emin olunamayan her durumda JSON
            }
        }

        // source (tipi 'from') ifadesinden 'to' tipinde yeni nesne üreten ifade; desteklenmiyorsa null.
        private static Expression? BuildMap(Expression source, Type from, Type to, HashSet<Type> inProgress)
        {
            if (!IsMappableShape(from, isTarget: false) || !IsMappableShape(to, isTarget: true)) return null;
            if (!inProgress.Add(to)) return null; // döngüsel şekil -> JSON (o da derinlik sınırıyla hata verir)

            try
            {
                var sourceProps = from.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod is { IsPublic: true })
                    .ToList();
                if (sourceProps.Any(HasJsonAttribute)) return null;
                // Aynı isimde birden fazla (gizleme/new) -> JSON'un davranışı karmaşık, karışma.
                if (sourceProps.GroupBy(p => p.Name).Any(g => g.Count() > 1)) return null;

                var targetProps = to.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0).ToList();
                if (targetProps.Any(HasJsonAttribute)) return null;
                if (targetProps.GroupBy(p => p.Name).Any(g => g.Count() > 1)) return null;

                var targetVar = Expression.Variable(to, "t");
                var block = new List<Expression> { Expression.Assign(targetVar, Expression.New(to)) };

                // Kaynakta değeri null olan referans tipli bir property JSON'da "null" yazılır ve hedefe null atanır.
                var srcVar = Expression.Variable(from, "s");
                block.Insert(0, Expression.Assign(srcVar, source));

                foreach (var sp in sourceProps)
                {
                    var tp = targetProps.FirstOrDefault(p => p.Name == sp.Name);
                    if (tp == null) continue; // hedefte yok -> atla (JSON da atlar)
                    if (tp.SetMethod is not { IsPublic: true } || IsInitOnly(tp))
                    {
                        // Hedefte okunur-ama-yazılamaz: JSON (varsayılan ayarlarla) atlar; init-only'yi JSON YAZAR -> karışma.
                        if (IsInitOnly(tp)) return null;
                        continue;
                    }

                    var read = Expression.Property(srcVar, sp);
                    Expression? value;
                    if (sp.PropertyType == tp.PropertyType && EvokerValueConverter.IsLeafType(sp.PropertyType) && sp.PropertyType != typeof(byte[]))
                    {
                        value = read;
                    }
                    else if (!EvokerValueConverter.IsLeafType(sp.PropertyType) && !EvokerValueConverter.IsLeafType(tp.PropertyType)
                             && !sp.PropertyType.IsValueType && !tp.PropertyType.IsValueType)
                    {
                        // İç içe referans tipi: null ise null, değilse aynı kurallarla eşle (aynı tip olsa bile KOPYA - JSON gibi).
                        var inner = BuildMap(read, sp.PropertyType, tp.PropertyType, inProgress);
                        if (inner == null) return null;
                        value = Expression.Condition(
                            Expression.Equal(read, Expression.Constant(null, sp.PropertyType)),
                            Expression.Constant(null, tp.PropertyType),
                            inner);
                    }
                    else
                    {
                        return null; // farklı basit tipler (int -> long, enum -> int ...), byte[] (JSON kopyalar) ya da struct iç içe: JSON'un kuralları
                    }

                    block.Add(Expression.Assign(Expression.Property(targetVar, tp), value));
                }

                block.Add(targetVar);
                return Expression.Block(new[] { srcVar, targetVar }, block);
            }
            finally
            {
                inProgress.Remove(to);
            }
        }

        private static bool IsMappableShape(Type t, bool isTarget)
        {
            if (EvokerValueConverter.IsLeafType(t) || t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition) return false;
            if (t.IsArray || t.IsPointer || t.IsByRef || t == typeof(object)) return false;
            if (typeof(IEnumerable).IsAssignableFrom(t)) return false;           // koleksiyonlar JSON'da dizi/sözlük
            if (Nullable.GetUnderlyingType(t) != null) return false;
            if (t.IsGenericType && t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) return false; // KeyValuePair, Tuple...
            if (t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) return false;                     // framework tipleri: JSON'a bırak
            if (HasJsonAttribute(t)) return false;
            if (isTarget && !t.IsValueType && t.GetConstructor(Type.EmptyTypes) == null) return false;
            return true;
        }

        private static bool HasJsonAttribute(MemberInfo m) =>
            m.CustomAttributes.Any(a => a.AttributeType.Namespace == "System.Text.Json.Serialization");

        private static bool IsInitOnly(PropertyInfo p) =>
            p.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit") == true;
    }
}
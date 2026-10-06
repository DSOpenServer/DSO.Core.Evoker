using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace DSO.Core.Evoker.Description
{
    /// <summary>
    /// "Doldur ve gönder" komut şablonları. Parametre/property tipinden JSON iskeleti üretir:
    /// sayılar 0, metin "", bool false, tarih "2026-01-01T00:00:00", enum ilk değerin adı, koleksiyon [eleman iskeleti],
    /// sözlük {}, nesne { property: iskelet } (en fazla 3 seviye, döngüsel tiplerde null). Optional parametrelerde
    /// metodun KENDİ varsayılan değeri yazılır.
    ///
    /// Tip kontrolleri İSİMLE yapılır - MetadataLoadContext tipleriyle de (DLL çalıştırılmadan) çalışsın diye.
    /// </summary>
    public static class EvokerSampleBuilder
    {
        private const int MaxDepth = 3;

        public static JsonElement Command(string op, string member, JsonElement? args = null, JsonElement? value = null)
        {
            return Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("op", op);
                w.WriteString("member", member);
                if (args.HasValue) { w.WritePropertyName("args"); args.Value.WriteTo(w); }
                if (value.HasValue) { w.WritePropertyName("value"); value.Value.WriteTo(w); }
                w.WriteEndObject();
            });
        }

        /// <summary>İsimli argüman nesnesi: { "parametreAdı": iskelet-ya-da-varsayılan, ... }.</summary>
        public static JsonElement ArgsObject(ParameterInfo[] parameters, Assembly? home = null)
        {
            return Build(w =>
            {
                w.WriteStartObject();
                foreach (var p in parameters)
                {
                    if (p.ParameterType.IsByRef && !p.IsIn) continue; // ref/out: değer gönderilmez
                    w.WritePropertyName(p.Name ?? "arg" + p.Position);
                    var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
                    var (has, def) = EvokerDescriber.DefaultOf(p);
                    if (has) WriteDefault(w, t, def);
                    else WriteSkeleton(w, t, 0, new HashSet<string>());
                }
                w.WriteEndObject();
            });
        }

        public static JsonElement Skeleton(Type t, Assembly? home = null) =>
            Build(w => WriteSkeleton(w, t, 0, new HashSet<string>()));

        private static void WriteDefault(Utf8JsonWriter w, Type t, object? def)
        {
            if (def == null) { w.WriteNullValue(); return; }
            var u = Unwrap(t);
            if (u.IsEnum) { var n = EvokerDescriber.EnumName(u, def); if (n != null) { w.WriteStringValue(n); return; } }
            switch (def)
            {
                case string s: w.WriteStringValue(s); break;
                case bool b: w.WriteBooleanValue(b); break;
                case char c: w.WriteStringValue(c.ToString()); break;
                case decimal m: w.WriteNumberValue(m); break;
                case double d: w.WriteNumberValue(d); break;
                case float f: w.WriteNumberValue(f); break;
                case DateTime dt: w.WriteStringValue(dt); break;
                case IConvertible conv when IsIntegral(def.GetType()): w.WriteNumberValue(conv.ToInt64(null)); break;
                default: w.WriteStringValue(def.ToString()); break;
            }
        }

        private static void WriteSkeleton(Utf8JsonWriter w, Type t, int depth, HashSet<string> path)
        {
            var u = Unwrap(t);
            string fn = u.FullName ?? u.Name;
            switch (fn)
            {
                case "System.String": case "System.Char": case "System.Uri": w.WriteStringValue(""); return;
                case "System.Boolean": w.WriteBooleanValue(false); return;
                case "System.Byte":
                case "System.SByte":
                case "System.Int16":
                case "System.UInt16":
                case "System.Int32":
                case "System.UInt32":
                case "System.Int64":
                case "System.UInt64":
                case "System.Single":
                case "System.Double":
                case "System.Decimal": w.WriteNumberValue(0); return;
                case "System.DateTime": w.WriteStringValue("2026-01-01T00:00:00"); return;
                case "System.DateTimeOffset": w.WriteStringValue("2026-01-01T00:00:00+00:00"); return;
                case "System.DateOnly": w.WriteStringValue("2026-01-01"); return;
                case "System.TimeOnly": case "System.TimeSpan": w.WriteStringValue("00:00:00"); return;
                case "System.Guid": w.WriteStringValue("00000000-0000-0000-0000-000000000000"); return;
                case "System.Byte[]": w.WriteStringValue(""); return; // base64
                case "System.Object": case "System.Text.Json.JsonElement": case "System.Text.Json.Nodes.JsonNode": w.WriteNullValue(); return;
            }
            if (u.IsEnum)
            {
                var first = u.GetFields(BindingFlags.Public | BindingFlags.Static).FirstOrDefault();
                if (first != null) w.WriteStringValue(first.Name); else w.WriteNumberValue(0);
                return;
            }
            if (depth >= MaxDepth || u.IsInterface && !IsCollection(u) || u.IsAbstract && !IsCollection(u) || u.IsPointer)
            {
                w.WriteNullValue();
                return;
            }
            if (IsDictionary(u)) { w.WriteStartObject(); w.WriteEndObject(); return; }
            var element = CollectionElement(u);
            if (element != null)
            {
                w.WriteStartArray();
                WriteSkeleton(w, element, depth + 1, path);
                w.WriteEndArray();
                return;
            }
            if (fn.StartsWith("System.") || !path.Add(fn)) { w.WriteNullValue(); return; } // framework tipi / döngü
            try
            {
                w.WriteStartObject();
                foreach (var p in u.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0 || p.GetSetMethod() == null) continue;
                    w.WritePropertyName(p.Name);
                    WriteSkeleton(w, p.PropertyType, depth + 1, path);
                }
                w.WriteEndObject();
            }
            finally { path.Remove(fn); }
        }

        private static Type Unwrap(Type t) =>
            t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1" ? t.GetGenericArguments()[0] : t;

        private static bool IsIntegral(Type t) => t.FullName is "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16"
            or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64";

        private static IEnumerable<Type> SelfAndInterfaces(Type t)
        {
            yield return t;
            Type[] ifaces;
            try { ifaces = t.GetInterfaces(); } catch { ifaces = Type.EmptyTypes; }
            foreach (var i in ifaces) yield return i;
        }

        private static bool IsDictionary(Type t) => SelfAndInterfaces(t).Any(i => i.IsGenericType &&
            i.GetGenericTypeDefinition().FullName is "System.Collections.Generic.IDictionary`2" or "System.Collections.Generic.IReadOnlyDictionary`2");

        private static bool IsCollection(Type t) => t.IsArray || CollectionElement(t) != null || IsDictionary(t);

        private static Type? CollectionElement(Type t)
        {
            if (t.IsArray) return t.GetElementType();
            if (t.FullName == "System.String") return null;
            var e = SelfAndInterfaces(t).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition().FullName == "System.Collections.Generic.IEnumerable`1");
            return e?.GetGenericArguments()[0];
        }

        private static JsonElement Build(Action<Utf8JsonWriter> write)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) write(w);
            using var doc = JsonDocument.Parse(ms.ToArray());
            return doc.RootElement.Clone();
        }
    }
}
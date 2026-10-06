using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace DSO.Core.Evoker.Description
{
    /// <summary>
    /// Bir <see cref="Type"/>'ın <see cref="EvokerTypeDescriptor"/>'ını üretir. Tip çalışan bir tip de olabilir,
    /// MetadataLoadContext'ten gelen (DLL çalıştırılmadan okunmuş) bir tip de - sadece CustomAttributeData /
    /// RawDefaultValue gibi iki ortamda da çalışan API'ler kullanılır, tip adları karşılaştırması isimle yapılır.
    ///
    /// Bilerek DIŞARIDA bırakılanlar: derleyicinin ürettiği her şey (backing field'lar, async/lambda kalıntıları),
    /// VB.NET'in '$' içeren alanları, get_/set_/add_/remove_ metotları, event'lerin arka plan alanları, System.Object'ten
    /// gelen ve override edilmemiş metotlar, static constructor.
    /// (Eskiden DSO.Core.Evoker.Plugins.Scanning.PluginInspector'ın içindeydi; plugin tarafı artık bunu kullanır.)
    /// </summary>
    public static class EvokerDescriber
    {
        private const string CompilerGeneratedAttr = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

        public static EvokerTypeDescriptor Describe(Type type, EvokerDescribeOptions? options = null)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            options ??= new EvokerDescribeOptions();
            var home = type.Assembly;
            var warnings = new List<string>();

            var flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                        | (options.IncludeNonPublic ? BindingFlags.NonPublic : 0);

            var ctors = new List<(EvokerConstructorDescriptor D, ConstructorInfo C)>();
            var methods = new List<(EvokerMethodDescriptor D, MethodInfo M)>();
            var props = new List<(EvokerPropertyDescriptor D, PropertyInfo P)>();
            var fields = new List<(EvokerFieldDescriptor D, FieldInfo F)>();
            var events = new List<EvokerEventDescriptor>();
            var seenMethods = new HashSet<string>();
            var seenMembers = new HashSet<string>();

            for (var level = type; level != null; level = options.IncludeInherited ? level.BaseType : null)
            {
                if (level != type && (level.Assembly != home || IsSystemRoot(level))) break;
                string? declaredIn = level == type ? null : Friendly(level, home);

                Safe(warnings, $"{level.Name} event'leri", () =>
                {
                    foreach (var e in level.GetEvents(flags))
                    {
                        if (IsNoise(e) || !seenMembers.Add("E:" + e.Name)) continue;
                        var add = e.GetAddMethod(true);
                        var invoke = e.EventHandlerType?.GetMethod("Invoke");
                        events.Add(new EvokerEventDescriptor
                        {
                            Name = e.Name,
                            Visibility = Visibility(add),
                            IsStatic = add?.IsStatic == true ? true : null,
                            HandlerType = e.EventHandlerType != null ? Friendly(e.EventHandlerType, home) : "?",
                            Arguments = NullIfEmpty(invoke?.GetParameters().Select(p => Param(p, home)).ToList()),
                            DeclaredIn = declaredIn
                        });
                    }
                });
                var eventNames = new HashSet<string>(level.GetEvents(flags).Select(e => e.Name));

                if (level == type)
                {
                    Safe(warnings, "constructor'lar", () =>
                    {
                        foreach (var c in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | (options.IncludeNonPublic ? BindingFlags.NonPublic : 0)))
                            ctors.Add((new EvokerConstructorDescriptor
                            {
                                Visibility = Visibility(c),
                                Parameters = NullIfEmpty(c.GetParameters().Select(p => Param(p, home)).ToList())
                            }, c));
                    });
                }

                Safe(warnings, $"{level.Name} property'leri", () =>
                {
                    foreach (var p in level.GetProperties(flags))
                    {
                        if (IsNoise(p) || !seenMembers.Add("P:" + p.Name)) continue;
                        var get = p.GetGetMethod(true);
                        var set = p.GetSetMethod(true);
                        if (!options.IncludeNonPublic) { if (get != null && !get.IsPublic) get = null; if (set != null && !set.IsPublic) set = null; }
                        if (get == null && set == null) continue;
                        bool initOnly = set != null && set.ReturnParameter.GetRequiredCustomModifiers()
                            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
                        props.Add((new EvokerPropertyDescriptor
                        {
                            Name = p.Name,
                            Type = Friendly(p.PropertyType, home),
                            Getter = get != null ? Visibility(get) : null,
                            Setter = set != null ? Visibility(set) : null,
                            IsInitOnly = initOnly ? true : null,
                            IsStatic = (get ?? set)!.IsStatic ? true : null,
                            IndexerParameters = NullIfEmpty(p.GetIndexParameters().Select(x => Param(x, home)).ToList()),
                            DeclaredIn = declaredIn
                        }, p));
                    }
                });

                Safe(warnings, $"{level.Name} field'ları", () =>
                {
                    foreach (var f in level.GetFields(flags))
                    {
                        if (IsNoise(f) || f.IsSpecialName) continue;
                        if (eventNames.Contains(f.Name) || (f.Name.EndsWith("Event") && eventNames.Contains(f.Name[..^5]))) continue;
                        if (!seenMembers.Add("F:" + f.Name)) continue;
                        object? constValue = null;
                        if (f.IsLiteral) { try { constValue = f.GetRawConstantValue(); } catch { } }
                        fields.Add((new EvokerFieldDescriptor
                        {
                            Name = f.Name,
                            Type = Friendly(f.FieldType, home),
                            Visibility = Visibility(f),
                            IsStatic = f.IsStatic && !f.IsLiteral ? true : null,
                            IsReadOnly = f.IsInitOnly ? true : null,
                            IsConst = f.IsLiteral ? true : null,
                            ConstValue = constValue,
                            DeclaredIn = declaredIn
                        }, f));
                    }
                });

                Safe(warnings, $"{level.Name} metotları", () =>
                {
                    foreach (var m in level.GetMethods(flags))
                    {
                        if (m.IsSpecialName || IsNoise(m)) continue;
                        var ps = m.GetParameters();
                        string sig = m.Name + "(" + string.Join(",", ps.Select(x => x.ParameterType.FullName)) + ")";
                        if (!seenMethods.Add(sig)) continue;

                        string ret = Friendly(m.ReturnType, home);
                        string? notCallable =
                            ps.Any(x => x.ParameterType.IsByRef && !x.IsIn) ? "ref/out parametre (değeri çağırana geri taşınamaz)"
                            : m.ContainsGenericParameters ? "açık generic metot (tip argümanı belirtilemiyor)"
                            : m.IsAbstract ? "abstract (gövdesi yok)"
                            : ps.Any(x => x.ParameterType.IsPointer) ? "pointer parametre"
                            : null;
                        string retFull = m.ReturnType.FullName ?? "";
                        var paramDescs = ps.Select(x => Param(x, home)).ToList();
                        methods.Add((new EvokerMethodDescriptor
                        {
                            Name = m.Name,
                            Visibility = Visibility(m),
                            ReturnType = ret,
                            Parameters = NullIfEmpty(paramDescs),
                            IsStatic = m.IsStatic ? true : null,
                            IsAsync = retFull.StartsWith("System.Threading.Tasks.Task") || retFull.StartsWith("System.Threading.Tasks.ValueTask") ? true : null,
                            IsVirtual = m.IsVirtual && !m.IsFinal && (m.Attributes & MethodAttributes.NewSlot) != 0 && !m.IsAbstract ? true : null,
                            IsAbstract = m.IsAbstract ? true : null,
                            IsOverride = m.IsVirtual && (m.Attributes & MethodAttributes.NewSlot) == 0 ? true : null,
                            GenericArguments = m.IsGenericMethodDefinition ? m.GetGenericArguments().Select(g => g.Name).ToList() : null,
                            DeclaredIn = declaredIn,
                            NotCallableReason = notCallable,
                            Signature = $"{(m.IsStatic ? "static " : "")}{ret} {m.Name}({string.Join(", ", paramDescs.Select(SignaturePart))})"
                        }, m));
                    }
                });
            }

            if (options.IncludeSamples)
            {
                Safe(warnings, "şablonlar", () =>
                {
                    foreach (var (d, c) in ctors) d.Sample = EvokerSampleBuilder.ArgsObject(c.GetParameters(), home);
                    foreach (var (d, m) in methods.Where(x => x.D.NotCallableReason == null))
                        d.Sample = EvokerSampleBuilder.Command("invoke", m.Name, args: EvokerSampleBuilder.ArgsObject(m.GetParameters(), home));
                    foreach (var (d, p) in props.Where(x => x.D.IndexerParameters == null))
                    {
                        if (d.Getter != null) d.Sample = EvokerSampleBuilder.Command("get", p.Name);
                        if (d.Setter != null && d.IsInitOnly != true) d.SampleSet = EvokerSampleBuilder.Command("set", p.Name, value: EvokerSampleBuilder.Skeleton(p.PropertyType, home));
                    }
                    foreach (var (d, f) in fields.Where(x => x.D.IsConst != true))
                    {
                        d.Sample = EvokerSampleBuilder.Command("get", f.Name);
                        if (d.IsReadOnly != true) d.SampleSet = EvokerSampleBuilder.Command("set", f.Name, value: EvokerSampleBuilder.Skeleton(f.FieldType, home));
                    }
                });
            }

            var descriptor = new EvokerTypeDescriptor
            {
                FullName = type.FullName ?? type.Name,
                Name = type.Name,
                Namespace = type.Namespace,
                AssemblyName = home.GetName().Name,
                Kind = Kind(type),
                BaseType = type.BaseType != null && !IsSystemRoot(type.BaseType) ? Friendly(type.BaseType, home) : null,
                Interfaces = NullIfEmpty(type.GetInterfaces().Select(i => Friendly(i, home)).OrderBy(x => x).ToList()),
                Constructors = NullIfEmpty(ctors.Select(x => x.D).ToList()),
                Methods = NullIfEmpty(methods.Select(x => x.D).OrderBy(m => m.DeclaredIn != null).ThenBy(m => m.Name).ToList()),
                Properties = NullIfEmpty(props.Select(x => x.D).OrderBy(p => p.DeclaredIn != null).ThenBy(p => p.Name).ToList()),
                Fields = NullIfEmpty(fields.Select(x => x.D).OrderBy(f => f.DeclaredIn != null).ThenBy(f => f.Name).ToList()),
                Events = NullIfEmpty(events.OrderBy(e => e.Name).ToList())
            };
            foreach (var w in warnings) descriptor.AddWarning(w);
            return descriptor;
        }

        /// <summary>
        /// Descriptor'daki field/property'lere o anki değerleri yazar. <paramref name="read"/> üye adıyla değeri okur
        /// (static üyeler için instance verilmez - okuyucu karar verir). Her üye ayrı okunur; okunamayan üye sadece kendi
        /// ValueError'ını alır. <paramref name="includeNonPublic"/> false iken public olmayan üyeler okunmaz.
        /// </summary>
        public static async Task CaptureValuesAsync(EvokerTypeDescriptor descriptor, Func<string, bool, Task<object?>> read,
            bool includeNonPublic, string source, int maxValueJsonLength = 4096, bool readStatic = false)
        {
            int failed = 0;
            foreach (var p in descriptor.Properties ?? new())
            {
                if (p.IsStatic == true && !readStatic) { p.ValueError = "static üye - değeri okunmuyor (instance değil)."; continue; }
                if (p.IndexerParameters != null) continue;
                if (p.Getter == null) { p.ValueError = "get erişimi yok (sadece set)."; continue; }
                if (p.DeclaredIn != null) { p.ValueError = $"temel sınıfta ({p.DeclaredIn}) tanımlı - değer okuma sadece tipin kendi üyeleri için."; continue; }
                if (p.Getter != MemberVisibility.Public && !includeNonPublic) { p.ValueError = "public olmayan get - hedef IncludeNonPublic=false ile yüklü."; continue; }
                if (!await TryReadAsync(() => read(p.Name, p.IsStatic == true), maxValueJsonLength, (v, t, e) => { p.Value = v; p.ValueTruncated = t; p.ValueError = e; }).ConfigureAwait(false)) failed++;
            }
            foreach (var f in descriptor.Fields ?? new())
            {
                if (f.IsConst == true) continue;
                if (f.IsStatic == true && !readStatic) { f.ValueError = "static üye - değeri okunmuyor (instance değil)."; continue; }
                if (f.DeclaredIn != null) { f.ValueError = $"temel sınıfta ({f.DeclaredIn}) tanımlı - değer okuma sadece tipin kendi üyeleri için."; continue; }
                if (f.Visibility != MemberVisibility.Public && !includeNonPublic) { f.ValueError = "public olmayan field - hedef IncludeNonPublic=false ile yüklü."; continue; }
                if (!await TryReadAsync(() => read(f.Name, f.IsStatic == true), maxValueJsonLength, (v, t, e) => { f.Value = v; f.ValueTruncated = t; f.ValueError = e; }).ConfigureAwait(false)) failed++;
            }

            descriptor.Values = new EvokerValuesInfo
            {
                CapturedUtc = DateTime.UtcNow,
                Source = source,
                Note = "Property değerleri okunurken get kodu ÇALIŞTIRILDI." + (failed > 0 ? $" {failed} üye okunamadı (bkz. ValueError)." : "")
            };
        }

        private static async Task<bool> TryReadAsync(Func<Task<object?>> read, int maxLen, Action<JsonElement?, bool?, string?> set)
        {
            try
            {
                var raw = await read().ConfigureAwait(false);
                if (raw == null) { set(null, null, null); return true; }
                var element = Conversion.EvokerJson.ToElement(raw);
                var text = element.GetRawText();
                if (text.Length > maxLen)
                {
                    using var doc = JsonDocument.Parse(JsonSerializer.Serialize(text[..maxLen] + "…"));
                    set(doc.RootElement.Clone(), true, null);
                }
                else set(element, null, null);
                return true;
            }
            catch (Exception ex)
            {
                var actual = ex is TargetInvocationException { InnerException: { } i } ? i : ex.InnerException != null && ex.GetType().Name == "PluginInvocationException" ? ex.InnerException : ex;
                set(null, null, $"{actual.GetType().Name}: {actual.Message}");
                return false;
            }
        }

        // ================= yardımcılar =================

        private static string SignaturePart(EvokerParameterDescriptor p)
        {
            string s = (p.Direction == EvokerParameterDirection.Out ? "out " : p.Direction == EvokerParameterDirection.Ref ? "ref " : "")
                       + (p.IsParams == true ? "params " : "") + p.Type + " " + p.Name;
            if (p.HasDefaultValue == true)
                s += " = " + (p.DefaultValue switch { null => "null", string str when !str.Contains('.') || p.Type == "string" => "\"" + str + "\"", bool b => b ? "true" : "false", var o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) });
            else if (p.IsOptional == true) s += " = default";
            return s;
        }

        internal static EvokerParameterDescriptor Param(ParameterInfo p, Assembly home)
        {
            var t = p.ParameterType;
            EvokerParameterDirection? dir = null;
            if (t.IsByRef) { dir = p.IsOut ? EvokerParameterDirection.Out : p.IsIn ? EvokerParameterDirection.In : EvokerParameterDirection.Ref; t = t.GetElementType()!; }

            var (hasDefault, def) = DefaultOf(p);
            // Enum varsayılanı metadata'da sayı olarak durur - okunur olsun diye enum adına çevir.
            if (def != null && t.IsEnum)
            {
                var name = EnumName(t, def);
                if (name != null) def = $"{t.Name}.{name}";
            }

            return new EvokerParameterDescriptor
            {
                Name = p.Name ?? "",
                Type = Friendly(t, home),
                Direction = dir,
                IsOptional = p.IsOptional ? true : null,
                HasDefaultValue = hasDefault ? true : null,
                DefaultValue = def,
                IsParams = p.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.ParamArrayAttribute") ? true : null
            };
        }

        /// <summary>Optional parametrenin varsayılanı - MetadataLoadContext'te de çalışır (RawDefaultValue + DecimalConstant).</summary>
        internal static (bool HasDefault, object? Value) DefaultOf(ParameterInfo p)
        {
            if (!p.IsOptional) return (false, null);
            bool has = false;
            object? def = null;
            try
            {
                var raw = p.RawDefaultValue;
                if (raw != DBNull.Value && raw != System.Reflection.Missing.Value) { has = true; def = raw; }
            }
            catch { }
            var dec = p.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.DecimalConstantAttribute");
            if (dec != null && dec.ConstructorArguments.Count == 5)
            {
                try
                {
                    var a = dec.ConstructorArguments;
                    def = new decimal(Convert.ToInt32(a[4].Value), Convert.ToInt32(a[3].Value), Convert.ToInt32(a[2].Value), Convert.ToByte(a[1].Value) != 0, Convert.ToByte(a[0].Value));
                    has = true;
                }
                catch { }
            }
            return (has, def);
        }

        internal static string? EnumName(Type enumType, object value) =>
            enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(f => { try { return Equals(Convert.ToInt64(f.GetRawConstantValue()), Convert.ToInt64(value)); } catch { return false; } })?.Name;

        private static bool IsNoise(MemberInfo m)
        {
            var n = m.Name;
            if (n.StartsWith("<") || n.Contains('$')) return true;
            if (m is MethodInfo mi && mi.DeclaringType?.FullName == "System.Object") return true;
            if (m is ConstructorInfo ci && ci.IsStatic) return true;
            try { return m.GetCustomAttributesData().Any(a => a.AttributeType.FullName == CompilerGeneratedAttr); }
            catch { return false; }
        }

        private static bool IsSystemRoot(Type t) =>
            t.FullName is "System.Object" or "System.ValueType" or "System.Enum" or "System.MulticastDelegate" or "System.Delegate";

        private static EvokerTypeKind Kind(Type t)
        {
            if (t.IsInterface) return EvokerTypeKind.Interface;
            if (t.IsEnum) return EvokerTypeKind.Enum;
            if (t.IsValueType) return EvokerTypeKind.Struct;
            if (t.BaseType?.FullName == "System.MulticastDelegate") return EvokerTypeKind.Delegate;
            if (t.IsAbstract && t.IsSealed) return EvokerTypeKind.StaticClass;
            if (t.IsAbstract) return EvokerTypeKind.AbstractClass;
            if (t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(m => m.Name == "<Clone>$")) return EvokerTypeKind.Record;
            return EvokerTypeKind.Class;
        }

        private static MemberVisibility Visibility(MethodBase? m)
        {
            if (m == null) return MemberVisibility.Private;
            if (m.IsPublic) return MemberVisibility.Public;
            if (m.IsFamilyOrAssembly) return MemberVisibility.ProtectedInternal;
            if (m.IsFamily) return MemberVisibility.Protected;
            if (m.IsAssembly) return MemberVisibility.Internal;
            if (m.IsFamilyAndAssembly) return MemberVisibility.PrivateProtected;
            return MemberVisibility.Private;
        }

        private static MemberVisibility Visibility(FieldInfo f)
        {
            if (f.IsPublic) return MemberVisibility.Public;
            if (f.IsFamilyOrAssembly) return MemberVisibility.ProtectedInternal;
            if (f.IsFamily) return MemberVisibility.Protected;
            if (f.IsAssembly) return MemberVisibility.Internal;
            if (f.IsFamilyAndAssembly) return MemberVisibility.PrivateProtected;
            return MemberVisibility.Private;
        }

        private static readonly Dictionary<string, string> Keywords = new()
        {
            ["System.Void"] = "void",
            ["System.Object"] = "object",
            ["System.String"] = "string",
            ["System.Boolean"] = "bool",
            ["System.Byte"] = "byte",
            ["System.SByte"] = "sbyte",
            ["System.Int16"] = "short",
            ["System.UInt16"] = "ushort",
            ["System.Int32"] = "int",
            ["System.UInt32"] = "uint",
            ["System.Int64"] = "long",
            ["System.UInt64"] = "ulong",
            ["System.Single"] = "float",
            ["System.Double"] = "double",
            ["System.Decimal"] = "decimal",
            ["System.Char"] = "char"
        };

        /// <summary>
        /// Okunur C# tipi adı: Task&lt;int&gt;, int?, List&lt;string&gt;, Dictionary&lt;string, Point&gt;, byte[].
        /// .NET'in kendi tipleri ve <paramref name="home"/> assembly'sinin tipleri KISA adla; diğerleri tam adla.
        /// </summary>
        public static string Friendly(Type t, Assembly? home = null)
        {
            if (t.IsByRef) return Friendly(t.GetElementType()!, home);
            if (t.IsPointer) return Friendly(t.GetElementType()!, home) + "*";
            if (t.IsArray) return Friendly(t.GetElementType()!, home) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (t.IsGenericParameter) return t.Name;
            if (t.FullName != null && Keywords.TryGetValue(t.FullName, out var kw)) return kw;

            if (t.IsGenericType)
            {
                var def = t.GetGenericTypeDefinition();
                var args = t.GetGenericArguments();
                if (def.FullName == "System.Nullable`1") return Friendly(args[0], home) + "?";
                string baseName = ShortOrFull(def, home);
                int tick = baseName.IndexOf('`');
                if (tick >= 0) baseName = baseName[..tick];
                return $"{baseName}<{string.Join(", ", args.Select(a => Friendly(a, home)))}>";
            }
            return ShortOrFull(t, home);
        }

        private static string ShortOrFull(Type t, Assembly? home)
        {
            bool shortName = (t.Namespace?.StartsWith("System") ?? false) || (home != null && t.Assembly == home);
            string name = shortName ? t.Name : (t.FullName ?? t.Name);
            if (t.IsNested && t.DeclaringType != null && shortName) name = ShortOrFull(t.DeclaringType, home) + "." + t.Name;
            return name.Replace('+', '.');
        }

        private static List<T>? NullIfEmpty<T>(List<T>? list) => list == null || list.Count == 0 ? null : list;

        private static void Safe(List<string> warnings, string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { warnings.Add($"{what} okunamadı: {ex.Message}"); }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSO.Core.Evoker.Description
{
    /// <summary>
    /// Bir tipin tanımı: constructor/metot (parametre, varsayılan)/property (get-set görünürlüğü)/field/event.
    /// Kaynak tip çalışan bir tip de olabilir, MetadataLoadContext ile DLL çalıştırılmadan okunmuş bir tip de
    /// (bkz. EvokerDescriber). İstenirse o anki değerler (Values) ve her üye için hazır komut şablonları (Sample).
    ///
    /// JSON: PascalCase, enum'lar metin, null alanlar yazılmaz; bool'lar sadece true iken yazılır.
    /// (Eskiden DSO.Core.Evoker.Plugins.Scanning.PluginTypeDescriptor idi.)
    /// </summary>
    public sealed class EvokerTypeDescriptor
    {
        public string FullName { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Namespace { get; init; }
        public string? AssemblyName { get; init; }
        public EvokerTypeKind Kind { get; init; }
        public string? BaseType { get; init; }
        public List<string>? Interfaces { get; init; }
        public List<EvokerConstructorDescriptor>? Constructors { get; init; }
        public List<EvokerMethodDescriptor>? Methods { get; init; }
        public List<EvokerPropertyDescriptor>? Properties { get; init; }
        public List<EvokerFieldDescriptor>? Fields { get; init; }
        public List<EvokerEventDescriptor>? Events { get; init; }

        /// <summary>Değerler alındıysa nereden/ne zaman; alınmadıysa null.</summary>
        public EvokerValuesInfo? Values { get; set; }

        public List<string>? Warnings { get; set; }

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };

        public string ToJson(bool indented = true) =>
            JsonSerializer.Serialize(this, indented ? JsonOptions : new JsonSerializerOptions(JsonOptions) { WriteIndented = false });

        internal void AddWarning(string w) => (Warnings ??= new List<string>()).Add(w);
    }

    public enum EvokerTypeKind { Class, StaticClass, AbstractClass, Record, Struct, Enum, Interface, Delegate }

    public enum MemberVisibility { Public, ProtectedInternal, Protected, Internal, PrivateProtected, Private }

    public enum EvokerParameterDirection { In, Out, Ref }

    public sealed class EvokerConstructorDescriptor
    {
        public MemberVisibility Visibility { get; init; }
        public List<EvokerParameterDescriptor>? Parameters { get; init; }
        /// <summary>Doldurulacak constructorArgs şablonu (isimli; describe options IncludeSamples=true iken).</summary>
        public JsonElement? Sample { get; set; }
    }

    public sealed class EvokerMethodDescriptor
    {
        public string Name { get; init; } = "";
        public MemberVisibility Visibility { get; init; }
        public string ReturnType { get; init; } = "void";
        public List<EvokerParameterDescriptor>? Parameters { get; init; }
        public bool? IsStatic { get; init; }
        /// <summary>Task / Task&lt;T&gt; / ValueTask dönüyor (çağrı bunu bekler).</summary>
        public bool? IsAsync { get; init; }
        public bool? IsVirtual { get; init; }
        public bool? IsAbstract { get; init; }
        public bool? IsOverride { get; init; }
        public List<string>? GenericArguments { get; init; }
        /// <summary>Üye bir temel sınıftan geliyorsa o sınıf (tip kendisi tanımladıysa null).</summary>
        public string? DeclaredIn { get; init; }
        /// <summary>Null = çağrılabilir. Dolu ise neden çağrılamadığı (ref/out, açık generic...).</summary>
        public string? NotCallableReason { get; init; }
        /// <summary>Hazır komut: { "op":"invoke", "member":..., "args":{...} } (IncludeSamples=true iken).</summary>
        public JsonElement? Sample { get; set; }
        /// <summary>Okunur imza: "Task&lt;int&gt; AddAsync(int a, int b = 5)".</summary>
        public string? Signature { get; init; }
    }

    public sealed class EvokerParameterDescriptor
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public EvokerParameterDirection? Direction { get; init; }
        public bool? IsOptional { get; init; }
        public bool? HasDefaultValue { get; init; }
        /// <summary>Optional parametrenin varsayılan değeri (HasDefaultValue=true ve bu alan yoksa varsayılan null'dır).</summary>
        public object? DefaultValue { get; init; }
        /// <summary>C# params / VB ParamArray.</summary>
        public bool? IsParams { get; init; }
    }

    public sealed class EvokerPropertyDescriptor
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        /// <summary>Get erişiminin görünürlüğü (get yoksa null).</summary>
        public MemberVisibility? Getter { get; init; }
        /// <summary>Set erişiminin görünürlüğü (set yoksa null) - ör. Getter=Public, Setter=Private.</summary>
        public MemberVisibility? Setter { get; init; }
        public bool? IsInitOnly { get; init; }
        public bool? IsStatic { get; init; }
        public List<EvokerParameterDescriptor>? IndexerParameters { get; init; }
        public string? DeclaredIn { get; init; }

        public JsonElement? Value { get; set; }
        public bool? ValueTruncated { get; set; }
        public string? ValueError { get; set; }

        /// <summary>Hazır okuma komutu { "op":"get", ... } (IncludeSamples=true iken).</summary>
        public JsonElement? Sample { get; set; }
        /// <summary>Hazır yazma komutu { "op":"set", ..., "value":... } (yazılabilirse).</summary>
        public JsonElement? SampleSet { get; set; }
    }

    public sealed class EvokerFieldDescriptor
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public MemberVisibility Visibility { get; init; }
        public bool? IsStatic { get; init; }
        public bool? IsReadOnly { get; init; }
        public bool? IsConst { get; init; }
        public object? ConstValue { get; init; }
        public string? DeclaredIn { get; init; }

        public JsonElement? Value { get; set; }
        public bool? ValueTruncated { get; set; }
        public string? ValueError { get; set; }

        public JsonElement? Sample { get; set; }
        public JsonElement? SampleSet { get; set; }
    }

    public sealed class EvokerEventDescriptor
    {
        public string Name { get; init; } = "";
        public MemberVisibility Visibility { get; init; }
        public bool? IsStatic { get; init; }
        public string HandlerType { get; init; } = "";
        /// <summary>Handler'ın parametreleri (event argümanlarının sırası).</summary>
        public List<EvokerParameterDescriptor>? Arguments { get; init; }
        public string? DeclaredIn { get; init; }
    }

    public sealed class EvokerValuesInfo
    {
        public DateTime CapturedUtc { get; init; } = DateTime.UtcNow;
        /// <summary>Değerlerin kaynağı: "InProcess", "Sandbox", "Instance"...</summary>
        public string Source { get; init; } = "";
        public string? Note { get; init; }
    }

    public sealed class EvokerDescribeOptions
    {
        /// <summary>private/protected/internal üyeler de listelensin (varsayılan: evet).</summary>
        public bool IncludeNonPublic { get; init; } = true;

        /// <summary>Tipin KENDİ assembly'sindeki temel sınıflardan gelen üyeler de (DeclaredIn ile işaretli). System.* temellerine inilmez.</summary>
        public bool IncludeInherited { get; init; } = true;

        /// <summary>Her metot/property/field/constructor için hazır komut şablonu (Sample) üret.</summary>
        public bool IncludeSamples { get; init; }

        /// <summary>O anki field/property değerlerini oku (hedef destekliyorsa; property getter'ları ÇALIŞIR).</summary>
        public bool IncludeValues { get; init; }

        /// <summary>Bir değerin JSON'u bundan uzunsa kırpılır.</summary>
        public int MaxValueJsonLength { get; init; } = 4096;
    }
}
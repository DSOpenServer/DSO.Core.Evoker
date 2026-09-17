# DSO.Core.Evoker

**Çağır, üret, eriş — hepsi cache'li, hepsi ölçülmüş, hiçbiri tahmine dayanmıyor.**

`DSO.Core.Evoker`, .NET'te **runtime'da metot çağırmak**, **runtime'da yeni tip üretmek** ve **o tiplerin property/metotlarına düşük-overhead erişmek** için tasarlanmış, bağımlılıksız bir çekirdek kütüphanedir. Reflection'ın esnekliğini korurken, reflection'ın maliyetini (yeniden çözümleme, boxing, allocation) ortadan kaldırmayı hedefler.

---

## Neden var

.NET ekosisteminde bu problemi çözen birkaç farklı yaklaşım var, her birinin kendine göre bir bedeli oluyor:

- **Klasik reflection tabanlı çağırma yaklaşımları** her çağrıda metodu isimle yeniden arar, argümanları boxlar, çoğu zaman "hangi instance üzerinde çalıştığını" derlenmiş koda gömer — ki bu da paylaşılan bir cache'te **instance karışması** gibi sinsi buglara yol açabilir (bu proje boyunca tam olarak böyle bir bug'ı bulup düzelttik — aşağıda anlatıyoruz).
- **Runtime tip üreten yaklaşımlar** genellikle her üretim için ayrı bir assembly açar; aynı şekle sahip yüzlerce tip üretmeniz gerektiğinde (bir ORM'in satır tipleri gibi) bu, gereksiz metadata/assembly yükü demektir.
- **`dynamic` anahtar kelimesi / DLR** her çağrı sitesinde kendi cache'ini tutar ama yine de doğrudan derlenmiş bir delegate çağrısından belirgin şekilde yavaştır, ve size gerçek bir CLR tipi vermez — güçlü-tipli API'lere (ORM'ler, JSON serializer'lar) geçiremezsiniz.
- **Sözlük/closure tabanlı dinamik nesne çözümleri** (her "hücre" için bir closure/delegate) esnektir ama gerçek bir tip üretmediği için interface implement edemez, ve hücre başına allocation'ları satır×kolon ile katlanarak büyür.

`DSO.Core.Evoker` bu üçünün de iyi taraflarını almaya çalışıyor: **gerçek CLR tipleri** üretiyor (herhangi bir güçlü-tipli API'ye verebilirsiniz), **derlenmiş delegate'ler** kullanıyor (DLR/reflection dispatch'i yok), ve bunu **instance-güvenli, boxing-farkında bir cache mimarisiyle** yapıyor.

---

## Öne çıkan özellikler

### 1. İki farklı erişim katmanı, iki farklı ihtiyaç için
- **`EvokerBuilder`**: "herhangi bir metodu isimle çağır" — esnek, genel amaçlı, `object[]` tabanlı.
- **`DynamicEntityAccessor`**: "bilinen bir property'yi bilinen bir tiple oku/yaz" — dar kapsamlı ama **ölçülmüş olarak sıfır allocation**.

İkisi birbirinin yerine değil, birbirini tamamlayan iki araç: ORM'inizin satır-okuma döngüsü ikincisini kullanırken, nadiren çağrılan dinamik bir kural motoru birincisini kullanabilir.

### 2. Instance-güvenli cache — gerçek bir bug'ı düzelterek öğrendik
Çoğu "derlenmiş delegate cache'le" yaklaşımının gözden kaçırdığı bir tuzak var: cache key'i sadece `(Tip, MetotAdı)` olursa, ve derlenen delegate içine **hangi instance/constructor argümanı kullanılacağı gömülürse**, aynı tip+metot kombinasyonuna sahip farklı çağrılar **birbirinin sonucunu ezer**. Bunu bu projede gerçek bir testle kanıtladık ve düzelttik: instance artık cache'in **dışında**, her çağrıda ayrı çözülüyor; cache sadece "bu metot nasıl çağrılır" bilgisini tutuyor.

### 3. Paylaşımlı modül + şema-bazlı tip cache'i
Aynı şemayı (property adı+tipi kombinasyonu) tekrar tekrar üretmeniz gerekmiyor — `DynamicTypeFactory` tek bir paylaşımlı `ModuleBuilder` kullanıyor ve aynı şema ikinci kez istendiğinde IL'i yeniden üretmeden cache'ten dönüyor. Gerçekten izole bir tip istiyorsanız (çoklu-kiracı senaryoları gibi) bunu bypass eden bir yol da var.

### 4. Ölçülmüş, iddia değil kanıt
Bu kütüphanedeki her performans iyileştirmesi gerçek bir benchmark ile doğrulandı (aşağıda rakamlar var). "Hızlı olmalı" değil, "şu ölçümde şu kadar hızlı" diyoruz.

### 5. Thread-safe
Aynı dinamik tip tanımını birden fazla thread'den eş zamanlı build etmeye çalışsanız bile tip/instance sadece bir kez üretilir (double-checked locking); hook mekanizmaları ince-taneli senkronizasyon kullanıyor, hot path'i kilitlemiyor.

---

## Performans — gerçek sayılar

200.000 satırlık bir materyalizasyon senaryosunda (.NET 8, Release build) ölçüldü:

| Yöntem | Süre | Toplam Alloc | Satır Başı Alloc |
|---|---|---|---|
| Reflection tabanlı (Activator + isimle çağırma) | 777 ms | 659 MB | ~3456 B |
| `DynamicEntityAccessor` (tipe özel derlenmiş erişim) | 4 ms | 9 MB | ~48 B |

**~194x hızlanma, ~%99 allocation azalması.**

Tipli erişim (`GetGetter<T>`/`GetSetter<T>`) için isim ile her seferinde yeniden çözümleme yapılsa bile **ölçülen allocation: 0.00 B/çağrı**. Tip bilmeden (`object` olarak) erişimde ise sadece **kaçınılmaz boxing kadar** maliyet var (`int` için 24B — teorik minimum, fazlası yok).

---

## Hızlı başlangıç

```csharp
using DSO.Core.Evoker;

// --- 1) Var olan bir metodu isimle çağırmak (EvokerEngine/EvokerBuilder) ---
var sonuc = EvokerEngine.InvokePublic<MusteriServisi, string>("Kaydet", "Ahmet", 30);

// --- 2) Runtime'da yeni bir "sınıf" üretmek ve kullanmak (DynamicClass) ---
var musteri = DynamicClass.CreateClass("Musteri")
    .AddProperty<int>("Id")
    .AddProperty<string>("Ad");

musteri.SetValue("Id", 1);
musteri.SetValue("Ad", "Ahmet");

Console.WriteLine(musteri.GetValue<string>("Ad")); // "Ahmet"
```

---

## Kapsamlı Özellik Rehberi

Aşağıdaki bölümler her bir sınıfın **tüm** public API yüzeyini, kısa açıklama ve kod örnekleriyle listeliyor — bir "hızlı referans" olarak kullanın.

### `EvokerEngine` — statik giriş noktası

```csharp
// Parametresiz/parametreli constructor ile hedefleme
EvokerEngine.Target<Musteri>().Invoke<string>("Selamla");
EvokerEngine.Target<Musteri>("ctorArg1", 42).Invoke<string>("Selamla");
EvokerEngine.Target("Namespace.Musteri", "ctorArg1").Execute("YapBirSey");
EvokerEngine.Target(typeof(Musteri)).Invoke<string>("Selamla");

// Doğrudan tip adıyla (string) çağırma
EvokerEngine.ResolveType("Namespace.Musteri"); // Type döner, cache'li

// Public/private metot çağırma kısayolları
EvokerEngine.InvokePublic<Musteri, string>("Ad", 1, "x");
EvokerEngine.InvokePrivate<Musteri, int>("GizliHesapla");
EvokerEngine.ExecutePublic<Musteri>("Kaydet");
EvokerEngine.ExecutePrivate<Musteri>("GizliIslem");

// Asenkron
await EvokerEngine.ExecutePublicAsync<Musteri>("KaydetAsync");
await EvokerEngine.InvokePublicAsync<Musteri, string>("GetirAsync");
```

### `EvokerBuilder` — esnek, isimle çağırma

```csharp
var builder = new EvokerBuilder(typeof(Musteri))
    .SetInstance(mevcutNesne)       // var olan bir nesneye bağlan
    // VEYA
    .SetConstructor("arg1", 42);    // yeni nesne için constructor argümanları

object? sonuc = builder.Invoke("MetotAdi", arg1, arg2);
string? tipli = builder.Invoke<string>("MetotAdi", arg1);
builder.Execute("VoidMetot", arg1);

await builder.ExecuteAsync("AsyncVoidMetot");
string? asy = await builder.InvokeAsync<string>("AsyncMetot");

// Derlenmiş delegate'i doğrudan almak isterseniz (döngüde tekrar tekrar çağıracaksanız):
Func<object[], string> hizliCagri = builder.GetFunc<string>("MetotAdi");
Action<object[]> hizliVoid = builder.GetAction("VoidMetot");
```

> **Not:** `ref`/`out` parametreli metotları `EvokerBuilder` ile çağırmayın — `object[]` tabanlı çağrı sözleşmesi bu değerleri çağırana geri taşıyamaz (eskiden sessizce kaybediyordu, artık açıkça `NotSupportedException` fırlatıyor). Böyle metotlar için düz reflection (`type.GetMethod(...).Invoke(instance, args)`) kullanın — `args` dizisi doğru şekilde güncellenir.

### `DynamicTypeFactory` — runtime'da tip üretimi

```csharp
var props = new Dictionary<string, Type> { { "Id", typeof(int) }, { "Ad", typeof(string) } };

// Şema cache'li (aynı şema tekrar istenirse aynı Type döner)
Type t1 = DynamicTypeFactory.CreateType("Musteri", props);

// Şema cache'ini bypass eder (her çağrı FARKLI, izole bir Type üretir)
Type t2 = DynamicTypeFactory.CreateUniqueType("Musteri", props);

// Sorgulama
bool dinamik = DynamicTypeFactory.IsDynamicType(t1);
var schema = DynamicTypeFactory.GetSchema(t1); // IReadOnlyList<(string Name, Type Type)>

// Cache bakımı
DynamicTypeFactory.ForgetSchema("Musteri", props);
DynamicTypeFactory.MaxSchemaCacheSize = 5000; // varsayılan sınırsız; FIFO tahliye (gerçek LRU değil)
```

`CreateType`/`CreateUniqueType`, `DSO.Core.Evoker.Extend` gibi üst katmanların interface/base-class implementasyonu, `ref`/`out`, generic metot ve event desteği eklemesine izin veren opsiyonel parametreler de alır (`configureType`, `methods`, `genericMethods`, `events`) — bunlar core'un kendi kullanımında hiç gerekmez, sadece genişletme noktasıdır.

### `DynamicEntityAccessor` — boxing'siz, tipe özel erişim

```csharp
Func<object> ctor = DynamicEntityAccessor.GetConstructor(type);
object instance = ctor();

Func<object, int> getId = DynamicEntityAccessor.GetGetter<int>(type, "Id");
Action<object, int> setId = DynamicEntityAccessor.GetSetter<int>(type, "Id");

setId(instance, 42);
int id = getId(instance); // boxing YOK

// Tip bilmeden erişim (object) - sınırda kaçınılmaz boxing dışında ekstra maliyet yok
Func<object, object> getAny = DynamicEntityAccessor.GetGetter<object>(type, "Id");

// Soğuk başlangıç ısıtma
DynamicEntityAccessor.Warmup(new[]
{
    (type, (IEnumerable<(string, Type)>)new[] { ("Id", typeof(int)), ("Ad", typeof(string)) })
});

// Cache bakımı
DynamicEntityAccessor.ForgetType(type);
DynamicEntityAccessor.MaxAccessorCacheSize = 5000;
int adet = DynamicEntityAccessor.CachedAccessorCount;
```

### `DynamicClass` — hepsini tek bir akışta toplayan fluent API

```csharp
// --- Oluşturma ---
var dc = DynamicClass.CreateClass("Musteri");            // varsayılan: şema cache'li, paylaşımlı
var izole = DynamicClass.CreateClass("Rapor",
    useSchemaCache: false, forgetOnDispose: true);         // gerçek bir kerelik şema için

// --- Property'ler ---
dc.AddProperty<int>("Id");
dc.AddProperty("Ad", typeof(string));

dc.SetValue("Id", 1);              // tipli, boxing'siz
dc.SetValue<int>("Id", 1);         // aynı şey, açık generic
dc.SetValue("Id", (object)1);      // tip bilmeden (boxing sınırda kaçınılmaz)

int id = dc.GetValue<int>("Id");
object? idObj = dc.GetValue("Id");

// --- Değişiklik hook'ları (instance'a özel, global cache'e DEĞİL) ---
dc.OnSet<int>("Id", (eski, yeni) => Console.WriteLine($"{eski} -> {yeni}"));
dc.OnGet<int>("Id", deger => Console.WriteLine($"okundu: {deger}"));
dc.RemoveOnSet("Id");
dc.RemoveOnGet("Id");

// --- Metotlar (gövdesi bir delegate ile atanır) ---
dc.AddMethod<Func<int, int, int>>("Topla");
dc.SetMethod<Func<int, int, int>>("Topla", (a, b) => a + b);
int toplam = dc.InvokeMethod<int>("Topla", 3, 4);
dc.InvokeMethod("VoidMetot", arg1);                        // dönüş değersiz
int asy = await dc.InvokeMethodAsync<int>("AsyncMetot");

// ref/out içeren bir metot eklemek istiyorsanız kendi delegate tipinizi tanımlayın:
public delegate bool TryParseDelegate(string s, out int sonuc);
dc.AddMethod<TryParseDelegate>("TryParse");
dc.SetMethod<TryParseDelegate>("TryParse", (string s, out int r) => int.TryParse(s, out r));
// Çağırmak için düz reflection kullanın (dc.InvokeMethod ref/out DESTEKLEMEZ):
var m = dc.Type.GetMethod("TryParse")!;
object?[] args = { "42", null };
m.Invoke(dc.RawInstance, args);
bool bulundu = (bool)args[0]!; // hayır, args[1] out değeri - dikkat sırasına

// --- Generic metotlar (type-erasure ile) ---
var template = typeof(ISomeInterface).GetMethod("Get")!; // T Get<T>()
dc.AddGenericMethod(template);
dc.SetGenericMethod("Get", (typeArgs, args) =>
{
    Type t = typeArgs[0];
    return t.IsValueType ? Activator.CreateInstance(t) : null; // ÖRNEK
});

// --- Event'ler ---
dc.AddEvent<EventHandler>("Degisti");
// abone olmak için gerçek tipe (bkz. Extend/As<T>()) ya da reflection'a ihtiyacınız var
dc.RaiseEvent("Degisti", dc.RawInstance, EventArgs.Empty);

// --- Attribute enjeksiyonu ---
dc.AddTypeAttribute<SerializableAttribute>();
dc.AddPropertyAttribute<ObsoleteAttribute>("EskiAlan", "kullanmayın");

// --- Var olan bir instance'ı sarmalama (ör. deserialize edilmiş bir nesne) ---
var sarmalanan = DynamicClass.Wrap(type, mevcutInstance);

// --- Genişletme noktası (DSO.Core.Evoker.Extend bunu kullanır) ---
dc.WithTypeConfigurator((typeBuilder, members) => { /* AddInterfaceImplementation, SetParent, vb. */ });

// --- Diğer ---
dc.Build();                 // build'i elle tetikler (opsiyonel, ilk kullanım zaten tetikler)
dc.Warmup();                 // property + metot cache'lerini önceden ısıtır
Type tip = dc.Type;
object ham = dc.RawInstance;
var sema = dc.Schema;        // IReadOnlyList<(string Name, Type Type)>
dc.Dispose();                // sadece forgetOnDispose:true ile anlamlı
```

---

## İlgili Projeler

Bu kütüphane bilerek **dar kapsamlı** tutuldu — davranış (interface/base class implementasyonu, gerçek metot gövdeleri) ve JSON gibi konular ayrı, opsiyonel projelere taşındı. İstemiyorsanız hiç referans vermenize gerek yok:

- **[`DSO.Core.Evoker.Extend`](../DSO.Core.Evoker.Extend/README.md)** — üretilen tiplerin var olan **interface**'leri veya **base class**'ları implement etmesini sağlar; property'lerin yanında gerçek davranışlı **metotlar**, **generic metotlar**, **`ref`/`out` parametreler**, **event**'ler ve **indexer**'lar dahil.
- **[`DSO.Core.Evoker.Json`](../DSO.Core.Evoker.Json/README.md)** — `System.Text.Json` ile sorunsuz entegrasyon; üretilen tipleri (iç içe olanlar dahil) otomatik serialize/deserialize eder, ekstra NuGet bağımlılığı gerektirmez.

---

## Bilinen Sınırlar (dürüstçe)

- Şema/accessor cache'lerindeki isteğe bağlı üst sınır (`MaxSchemaCacheSize`/`MaxAccessorCacheSize`) **FIFO** tahliye kullanır, gerçek bir LRU değildir.
- `ForgetType`/`ForgetSchema` sadece kendi dictionary cache'imizden çıkarır; CLR metadata'sı (`AssemblyBuilderAccess.Run` kullanıldığı için) process ömrü boyunca bellekte kalır.
- Üretilen tiplerin `ToString()`/`Equals()`/`GetHashCode()` davranışı `object`'ten miras — özelleştirilmemiştir.

## Gereksinimler

.NET 6.0+ · Bağımlılık yok (saf BCL: `System.Reflection.Emit`, `System.Linq.Expressions`)

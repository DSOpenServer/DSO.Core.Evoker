# DSO.Core.Evoker

> **Reflection'ın esnekliği, derlenmiş kodun hızı, JSON'un taşınabilirliği — tek bir bağımlılıksız çekirdekte.**

![.NET](https://img.shields.io/badge/.NET-6.0%20%7C%208.0-512BD4) ![Bağımlılık](https://img.shields.io/badge/ba%C4%9F%C4%B1ml%C4%B1l%C4%B1k-yok-brightgreen) ![Test](https://img.shields.io/badge/test-228%20kontrol-success)

`DSO.Core.Evoker`, .NET'te **isimle metot çağırmak**, **çalışma zamanında gerçek CLR tipleri üretmek**, **bu tiplerin
üyelerine boxing'siz erişmek** ve **herhangi bir sınıfı JSON komutlarla kullanılabilir hale getirmek** için yazılmış,
sıfır dış bağımlılıklı bir çekirdek kütüphanedir. Saf BCL kullanır: `System.Reflection.Emit`, `System.Linq.Expressions`,
`System.Text.Json`.

Bir kural motoru, ORM materyalizasyonu, plugin sistemi, dinamik form/rapor üreticisi, uzaktan yönetim API'si ya da
"bu DLL'deki şu metodu ekrandan çağırayım" ihtiyacı: hepsi aynı çekirdeğin üzerinde, aynı kurallarla çalışır.

---

## İçindekiler

- [Aile](#aile)
- [Neden Evoker?](#neden-evoker)
- [Rakamlar](#rakamlar)
- [Kurulum](#kurulum)
- [60 saniyede Evoker](#60-saniyede-evoker)
- [API Rehberi](#api-rehberi)
  - [EvokerEngine — statik giriş noktası](#evokerengine--statik-giriş-noktası)
  - [EvokerBuilder — isimle çağırma](#evokerbuilder--isimle-çağırma)
  - [DynamicEntityAccessor — sıfır allocation erişim](#dynamicentityaccessor--sıfır-allocation-erişim)
  - [DynamicTypeFactory — çalışma zamanında tip üretimi](#dynamictypefactory--çalışma-zamanında-tip-üretimi)
  - [DynamicDelegateTypeFactory — her imzaya delegate tipi](#dynamicdelegatetypefactory--her-imzaya-delegate-tipi)
  - [DynamicClass — hepsi tek bir akışta](#dynamicclass--hepsi-tek-bir-akışta)
  - [Commands — JSON komutlar](#commands--json-komutlar)
  - [Conversion — değer dönüştürme](#conversion--değer-dönüştürme)
  - [Description — tip tanımı ve şablonlar](#description--tip-tanımı-ve-şablonlar)
- [Plugin ve unload dostu tasarım](#plugin-ve-unload-dostu-tasarım)
- [Testler ve örnekler](#testler-ve-örnekler)
- [Bilinen sınırlar](#bilinen-sınırlar)

---

## Aile

| Paket | Ne yapar |
|---|---|
| **DSO.Core.Evoker** (bu paket) | Çağırma, tip üretimi, boxing'siz erişim, JSON komutlar, katalog, tip tanımı |
| [DSO.Core.Evoker.Extend](https://github.com/DSOpenServer/DSO.Core.Evoker.Extend/blob/main/README.md) | Üretilen tiplere **interface / base class** implementasyonu, generic metot, `ref`/`out`, event, indexer |
| [DSO.Core.Evoker.Json](https://github.com/DSOpenServer/DSO.Core.Evoker.Json/blob/main/README.md) | Üretilen tipleri `System.Text.Json` ile tek satırda serialize / deserialize |
| [DSO.Core.Evoker.Api](https://github.com/DSOpenServer/DSO.Core.Evoker.Api/blob/main/README.md) | Katalogdaki her hedefi **ASP.NET Core** uçlarından JSON komutla kullanma |
| [DSO.Core.Evoker.Plugins](https://github.com/DSOpenServer/DSO.Core.Evoker.Plugins/blob/main/README.md) | DLL plugin'leri **ayrı process'te (sandbox)** ya da **unload edilebilir context'te** çalıştırma, canlı mod geçişi |
| [DSO.Core.Evoker.Plugins.Api](https://github.com/DSOpenServer/DSO.Core.Evoker.Plugins.Api/blob/main/README.md) | Plugin yönetimini (tarama, kayıt, aktif/pasif, komut) hazır REST uçları olarak sunma |

---

## Neden Evoker?

Çalışma zamanında "isimle bir şey yapmak" için piyasadaki yaygın yaklaşımların her biri bir bedel ödetir:

| Yaygın yaklaşım | Bedeli | Evoker'ın cevabı |
|---|---|---|
| **Klasik reflection** (`MethodInfo.Invoke`) | Her çağrıda argüman dizisi, boxing, güvenlik kontrolleri; ~25 kat yavaş | Bir kez derlenen expression-tree delegate'leri; tipli sürümde **0 byte allocation** |
| **`dynamic` / dinamik dil runtime'ı** | Gerçek tip yok; güçlü tipli API'lere (ORM, serializer, DI) verilemez; çağrı noktası başına cache | **Gerçek CLR tipleri** üretir; interface implement eder, her API'ye verilebilir |
| **Sözlük / property-bag tabanlı dinamik nesneler** | Interface yok, hücre başına allocation, tip güvenliği yok | IL ile üretilmiş gerçek property'ler, şema cache'i, tipli getter/setter |
| **Merkezi interceptor'lı proxy çatıları** | Her çağrı tek bir interceptor'dan geçer; ağır, ince kontrol zor | Metot başına bağımsız forwarder, metot başına ayrı delegate |
| **"Önce derle, sonra çağır" kod üreteçleri** | Derleyici bağımlılığı, saniyeler süren üretim, büyük bellek | Roslyn yok; Reflection.Emit ile milisaniyeler |

Bunların üstüne, sıradan kütüphanelerin genelde hiç düşünmediği şeyler:

- **Instance-güvenli cache.** Derlenmiş delegate'ler instance'ı içinde *taşımaz*. Aynı tip + metot için farklı nesnelerle
  çalışan iki builder birbirinin sonucunu asla ezemez. Bu hata türü pek çok "hızlı reflection" çözümünde sessizce yaşar.
- **Akıllı overload seçimi.** Önce birebir ad, sonra büyük/küçük harf duyarsız ad (VB.NET dostu). Sonra argüman sayısı,
  optional parametreler, tip uyumu. JSON komutlarda ise ek olarak sayısal tercih (int → long → …) ve `params` desteği var.
- **Unload dostu.** Her cache'in bir "unut" kapısı var (`ForgetType`, `ForgetAssembly`). Collectible AssemblyLoadContext'ler
  gerçekten bellekten atılabilir; bu, [Plugins](https://github.com/DSOpenServer/DSO.Core.Evoker.Plugins/blob/main/README.md) paketinde testle doğrulanmıştır.
- **JSON-native.** Herhangi bir sınıf, hiçbir değişiklik yapmadan `{ "op":"invoke", "member":"Kes", "args":{...} }`
  komutlarıyla kullanılabilir. Tanım (describe) ve hazır komut şablonları da otomatik üretilir.

---

## Rakamlar

Ölçümler TestKit içindeki `PerfBench` ile .NET 8, Release derlemesinde alındı. Mutlak değerler makineye göre değişir;
**oranlar** belirleyicidir.

### Çağırma

| Yol | Süre / çağrı | Allocation / çağrı |
|---|---:|---:|
| Doğrudan derlenmiş, tipli delegate (teorik alt sınır) | 3.7 ns | 0 B |
| `MethodInfo.Invoke` (klasik reflection) | 92.4 ns | 112 B |
| `EvokerBuilder.Invoke<int>("Add", a, b)` | ~50 ns | 88 B (çağıranın `object[]`'i) |
| `EvokerBuilder.GetFunc<int>(...)` (önceden çözülmüş) | ~40 ns | 88 B |
| **`EvokerBuilder.GetFunc<int,int,int>("Add")`** | **~8–10 ns** | **0 B** |

### Property / field erişimi

| Yol | Süre | Allocation |
|---|---:|---:|
| `FieldInfo.GetValue` (klasik reflection) | 76.3 ns | 24 B |
| **`DynamicEntityAccessor` getter delegate'i** | **4.1 ns** | **0 B** |
| `EvokerBuilder.GetValue<int>("Counter")` | 43 ns | 0 B |

### Materyalizasyon (200.000 satır)

| Yöntem | Süre | Toplam allocation | Satır başı |
|---|---:|---:|---:|
| Reflection (Activator + isimle set) | 777 ms | 659 MB | ~3456 B |
| **`DynamicEntityAccessor`** | **4 ms** | **9 MB** | **~48 B** |

**~194 kat hız, ~%99 daha az allocation.**

### Tip çözme

| İşlem | Süre |
|---|---:|
| `EvokerEngine.ResolveType` cache isabeti | 23 ns, 0 B |
| Bulunamayan ad (ilk sorgu: tüm assembly'ler taranır) | ~15 ms |
| Aynı bulunamayan ad tekrar (negatif cache) | ~45 µs |

---

## Kurulum

Proje referansı olarak ekleyin:

```xml
<ProjectReference Include="..\DSO.Core.Evoker\DSO.Core.Evoker.csproj" />
```

- **Hedefler:** `net6.0` ve `net8.0` (çoklu hedef).
- **Bağımlılık:** yok.
- **Ad alanları:** `DSO.Core.Evoker`, `DSO.Core.Evoker.Commands`, `DSO.Core.Evoker.Conversion`, `DSO.Core.Evoker.Description`.

---

## 60 saniyede Evoker

```csharp
using DSO.Core.Evoker;
using DSO.Core.Evoker.Commands;

// 1) Var olan bir metodu isimle çağır
string? s = EvokerEngine.InvokePublic<MusteriServisi, string>("Kaydet", "Ahmet", 30);

// 2) Sıkı döngü için: bir kez çöz, tipli delegate al - boxing yok, allocation yok
var topla = new EvokerBuilder(typeof(Hesap)).SetInstance(hesap).GetFunc<int, int, int>("Topla");
int t = topla(3, 4);

// 3) Çalışma zamanında gerçek bir tip üret
var musteri = DynamicClass.CreateClass("Musteri")
    .AddProperty<int>("Id")
    .AddProperty<string>("Ad");
musteri.SetValue("Id", 1).SetValue("Ad", "Ayşe");
Console.WriteLine(musteri.GetValue<string>("Ad"));        // Ayşe

// 4) Herhangi bir sınıfı JSON komutla kullan
var hedef = new EvokerTarget(typeof(FaturaServisi));        // Singleton
var sonuc = await hedef.ExecuteAsync(EvokerCommand.Parse(
    "{ \"op\": \"invoke\", \"member\": \"Kes\", \"args\": { \"musteriKodu\": \"C001\", \"tutar\": 150.5 } }"));
Console.WriteLine(sonuc.ToJson());   // {"success":true,"result":{...},"elapsedMs":0.42}
```

---

## API Rehberi

Bu bölüm, her sınıfın **tüm public yüzeyini** örnekleriyle listeler; kütüphaneyi kullanırken başvuru kaynağı olarak
kullanabilirsiniz.

### EvokerEngine — statik giriş noktası

Tek satırlık çağrılar ve isimden tip çözme için.

```csharp
// --- Builder başlatma ---
EvokerBuilder b1 = EvokerEngine.Target<Musteri>();                       // parametresiz constructor
EvokerBuilder b2 = EvokerEngine.Target<Musteri>("bağlantı", 42);          // constructor argümanlarıyla
EvokerBuilder b3 = EvokerEngine.Target("Acme.Musteri", "bağlantı");       // tip adıyla
EvokerBuilder b4 = EvokerEngine.Target(typeof(Musteri));

// --- Tek satır çağrılar ---
object?  r1 = EvokerEngine.Invoke("Acme.Musteri", "Hesapla", includeNonPublic: false, 1, 2);
string?  r2 = EvokerEngine.InvokePublic<Musteri, string>("Selamla", "Ali");
int?     r3 = EvokerEngine.InvokePrivate<Musteri, int>("GizliHesap");
EvokerEngine.Execute("Acme.Musteri", "Temizle");
EvokerEngine.ExecutePublic<Musteri>("Kaydet");
EvokerEngine.ExecutePrivate<Musteri>("IcIslem");

// --- Async ---
await EvokerEngine.ExecutePublicAsync<Musteri>("KaydetAsync");
await EvokerEngine.ExecutePrivateAsync<Musteri>("GizliAsync");
await EvokerEngine.ExecutePublicAsync("Acme.Musteri", "KaydetAsync");
await EvokerEngine.ExecutePrivateAsync("Acme.Musteri", "GizliAsync");
string? r4 = await EvokerEngine.InvokePublicAsync<Musteri, string>("GetirAsync", 5);
string? r5 = await EvokerEngine.InvokePrivateAsync<Musteri, string>("GizliGetirAsync");

// --- Tip çözme ---
Type t = EvokerEngine.ResolveType("Musteri");         // kısa ad, tam ad ya da assembly-qualified ad
EvokerEngine.ClearTypeCache();                         // bulunan + bulunamayan ad cache'lerini temizler
```

**`ResolveType` arama sırası:**

1. `Type.GetType` (assembly-qualified ad).
2. Yüklü tüm assembly'lerde, şu sırayla: tam ad birebir → tam ad büyük/küçük harf duyarsız → kısa ad birebir → kısa ad
   duyarsız.

Bir seviyede birden fazla **farklı** tip eşleşirse (ör. iki plugin'de de `Result` sınıfı varsa) Evoker tahmin etmez:
`AmbiguousMatchException` ile adayları listeler. Kısmen yüklenebilen assembly'ler (`ReflectionTypeLoadException`)
aramayı düşürmez. Collectible context'lerdeki tipler **zayıf referansla** cache'lenir: plugin unload'u engellenmez ve
reload sonrası eski tip dönmez. Bulunamayan adlar da cache'lenir; yeni bir assembly yüklendiğinde bu cache kendiliğinden
boşalır.

---

### EvokerBuilder — isimle çağırma

Esnek, genel amaçlı çağırma motoru. `object[]` argümanlarla çalışır; sıkı döngüler için tipli delegate'ler sunar.

#### Oluşturma ve instance modu

```csharp
var b = new EvokerBuilder(typeof(Musteri), includeNonPublic: false);

b.SetInstance(mevcutNesne);        // SABİT nesne: her çağrı bu nesne üzerinde (durum korunur)
// ya da
b.SetConstructor("bağlantı", 42);  // her çağrıda bu argümanlarla YENİ nesne
// ikisi de verilmezse: her çağrıda parametresiz (ya da tüm parametreleri optional) constructor ile yeni nesne
// static metotlar için nesne hiç oluşturulmaz

Type   tip       = b.Type;
object? sabit    = b.Instance;           // sadece SetInstance verildiyse
bool   gizliler  = b.IncludeNonPublic;
object yeni      = b.CreateInstance();   // builder kurallarıyla bir nesne
```

#### Çağırma

```csharp
object?  o  = b.Invoke("Hesapla", 3, 4);
int?     i  = b.Invoke<int>("Hesapla", 3, 4);
b.Execute("Temizle");

await b.ExecuteAsync("KaydetAsync", kayit);            // Task döndüren metot
string? s = await b.InvokeAsync<string>("GetirAsync", 5);   // Task<T> döndüren metot
```

#### Metot seçme kuralları (`FindMethod`)

1. İsim önce **birebir** aranır, bulunamazsa **büyük/küçük harf duyarsız** aranır (VB.NET'te `add` ile `Add` aynıdır).
2. Parametre sayısı argüman sayısına **eşit** olan adaylar önceliklidir. Yoksa argümandan **fazla** parametresi olup
   fazlalıkların hepsi **optional** olan adaylar kullanılır; eksikler metodun kendi varsayılan değerleriyle dolar
   (C# `= değer`, VB.NET `Optional`).
3. Birden fazla aday kalırsa argüman tiplerine bakılır: önce birebir tip, sonra atanabilir tip.
4. Uygun aday yoksa `MissingMethodException` fırlatılır; mesajda mevcut imzalar listelenir.

Aynı kuralları dışarıdan da kullanabilirsiniz:

```csharp
MethodInfo m1 = b.FindMethod("Hesapla", new object?[] { 3, 4 });
MethodInfo m2 = b.FindMethodByTypes("Hesapla", new Type?[] { typeof(int), typeof(int) });
IReadOnlyList<MethodInfo> adaylar = b.FindMethodCandidates("Hesapla", argCount: 2);
bool olur = EvokerBuilder.AcceptsArgCount(m1, 1);   // 1 argümanla çağrılabilir mi?
```

#### Constructor seçimi

Constructor'lar metotlarla **aynı kurallarla** seçilir: sayı, optional, tip uyumu, sayısal genişletme (ör. `int`
parametreye `long`). Değerler gerekirse `EvokerValueConverter` ile dönüştürülür. Tüm parametreleri optional olan bir
constructor "parametresiz" sayılır.

```csharp
ConstructorInfo c1 = b.FindConstructor(new object?[] { "Server=.", 30 });
ConstructorInfo c2 = b.FindConstructorByTypes(new Type?[] { typeof(string) });
IReadOnlyList<ConstructorInfo> hepsi = b.GetConstructors();
```

#### Önceden çözülmüş delegate'ler (`object[]` tabanlı)

```csharp
Func<object[], int> hesapla = b.GetFunc<int>("Hesapla", sampleArgs: new object[] { 0, 0 });
Action<object[]>    temizle = b.GetAction("Temizle");

int r = hesapla(new object[] { 3, 4 });
```

#### Tipli delegate'ler (boxing yok, allocation yok)

Sıkı döngüler için en hızlı yol: argüman ve dönüş tipleri derleme zamanında bilinir. Son tip parametresi, .NET'in
`Func<>` kuralındaki gibi dönüş tipidir.

```csharp
Func<int, int, int>       topla   = b.GetFunc<int, int, int>("Topla");          // 1–4 argüman
Func<string, decimal>     fiyat   = b.GetFunc<string, decimal>("Fiyat");
Action<string>            logla   = b.GetAction<string>("Logla");                // 1–4 argüman
Action<int, int, int, int> dortlu = b.GetAction<int, int, int, int>("Dort");

for (int i = 0; i < 10_000_000; i++) toplam += topla(i, 1);   // ~8–10 ns, 0 B
```

- Overload, `T1..Tn` tiplerine göre seçilir.
- Tip metodun parametresinden farklıysa (`int` → `long`, türetilmiş → taban) dönüşüm derlenir; mümkün değilse
  `InvalidCastException` fırlatılır.
- Verilmeyen optional parametreler varsayılanla dolar.
- `SetInstance` verilmişse nesne delegate'e bağlanır; verilmemişse her çağrıda çözülür.
- Sıfır argümanlı tipli sürüm bilerek yoktur; argümansız metotlarda `GetFunc<T>(ad)` zaten boxing yapmaz.

#### Property ve field erişimi (`EvokerBuilderPropertyExtensions`)

```csharp
var b = new EvokerBuilder(typeof(Ayarlar), includeNonPublic: true).SetInstance(ayarlar);

int boyut = b.GetValue<int>("BatchSize");        // property; yoksa aynı isimli field
b.SetValue("BatchSize", 500);
string gizli = b.GetValue<string>("_sifre");     // includeNonPublic ile private üyeler de
b.ForgetCache();                                 // bu tipin accessor cache'ini temizle
```

`GetValue`/`SetValue` yalnızca `SetInstance` ile sabit bir nesneye bağlı builder'larda anlamlıdır; değilse
`InvalidOperationException` fırlatılır.

#### Event'ler (`EvokerBuilderEventExtensions`)

Derleme zamanında delegate tipini bilmeden herhangi bir event'e abone olun. `EventHandler`, `EventHandler<T>`, özel
delegate'ler ve VB.NET'in ürettiği gizli delegate'lerin hepsi desteklenir.

```csharp
using IDisposable abonelik = b.AddEventHandler("DurumDegisti", args =>
    Console.WriteLine($"sender={args[0]}, değer={args[1]}"));

string[] eventler = b.GetEventNames();
// Dispose = abonelikten çık
```

Static event'ler de desteklenir. Instance event'leri için builder'ın `SetInstance` ile bağlı olması gerekir.

#### Cache yönetimi

```csharp
EvokerBuilder.ForgetType(typeof(PluginTipi));    // bu tipin TÜM derlenmiş delegate'lerini bırak (unload öncesi şart)
int adet = EvokerBuilder.CachedCountFor(typeof(PluginTipi));
```

> **`ref` / `out`:** `object[]` sözleşmesi bu değerleri çağırana geri taşıyamadığı için `ref`/`out` parametreli metotlar
> açık bir `NotSupportedException` ile reddedilir; değerler sessizce kaybolmaz. Bu tür metotlar için
> [DSO.Core.Evoker.Extend](https://github.com/DSOpenServer/DSO.Core.Evoker.Extend/blob/main/README.md) ya da düz reflection kullanın.

---

### DynamicEntityAccessor — sıfır allocation erişim

ORM satır okuma döngüsü gibi sıcak yollar için. Bilinen bir property, bilinen bir tiple, **boxing olmadan** okunur ve
yazılır.

```csharp
Func<object> yeni = DynamicEntityAccessor.GetConstructor(tip);                 // parametresiz ctor (cache'li)
Func<object> yeniGizli = DynamicEntityAccessor.GetConstructor(tip, includeNonPublic: true);

Func<object, int>   getId = DynamicEntityAccessor.GetGetter<int>(tip, "Id");
Action<object, int> setId = DynamicEntityAccessor.GetSetter<int>(tip, "Id");
Func<object, object> getAny = DynamicEntityAccessor.GetGetter<object>(tip, "Id");   // tip bilinmiyorsa

object e = yeni();
setId(e, 42);
int id = getId(e);                                                              // 0 B
```

- Property yoksa **aynı isimli field**'a düşer.
- `includeNonPublic: true` ile private/protected/internal üyeler de görülür.
- Cache anahtarı struct'tır; cache isabetinde bile string üretilmez.

```csharp
// Soğuk başlangıç gecikmesini açılışa taşı
DynamicEntityAccessor.Warmup(new[]
{
    (tip, (IEnumerable<(string, Type)>)new[] { ("Id", typeof(int)), ("Ad", typeof(string)) })
});

// Cache yönetimi
DynamicEntityAccessor.MaxAccessorCacheSize = 5000;      // varsayılan sınırsız (FIFO tahliye)
int ctorAdedi     = DynamicEntityAccessor.CachedConstructorCount;
int accessorAdedi = DynamicEntityAccessor.CachedAccessorCount;
DynamicEntityAccessor.ForgetType(tip);
```

---

### DynamicTypeFactory — çalışma zamanında tip üretimi

Gerçek CLR tipleri üretir. Tüm tipler tek, paylaşımlı bir modülde üretilir ve aynı şema ikinci kez istendiğinde IL
yeniden üretilmez.

```csharp
var props = new Dictionary<string, Type> { ["Id"] = typeof(int), ["Ad"] = typeof(string) };

Type t1 = DynamicTypeFactory.CreateType("Musteri", props);        // şema cache'li: aynı şema -> aynı Type
Type t2 = DynamicTypeFactory.CreateUniqueType("Musteri", props);  // her çağrıda izole, yeni bir Type

bool bizim = DynamicTypeFactory.IsDynamicType(t1);
IReadOnlyList<(string Name, Type Type)>? sema = DynamicTypeFactory.GetSchema(t1);

DynamicTypeFactory.ForgetSchema("Musteri", props);               // şemayı cache'ten çıkar
DynamicTypeFactory.MaxSchemaCacheSize = 5000;                     // FIFO üst sınır
```

**Genişletme parametreleri** (çekirdek bunlara ihtiyaç duymaz; [Extend](https://github.com/DSOpenServer/DSO.Core.Evoker.Extend/blob/main/README.md) kullanır):

```csharp
Type t = DynamicTypeFactory.CreateType(
    className: "Kayit",
    properties: props,
    configureType: (typeBuilder, members) => { /* AddInterfaceImplementation, SetParent, DefineMethodOverride ... */ },
    methods: new() { ["Dogrula"] = typeof(Func<bool>) },                  // delegate'e yönlendiren metotlar
    genericMethods: new() { ["Getir"] = typeof(IDepo).GetMethod("Getir")! },
    events: new() { ["Degisti"] = typeof(EventHandler) });
```

`configureType` verildiğinde şema cache'i bilinçli olarak **devre dışı** kalır: aynı şema farklı yapılandırmalarla
farklı tipler üretebilir. `TypeMembers` yapısı, emisyonu biten property, metot, generic metot ve event builder'larını
verir. `InvokeGenericMethodDelegate` ise generic forwarder'ların çağırdığı public yardımcıdır.

---

### DynamicDelegateTypeFactory — her imzaya delegate tipi

`Func<>`/`Action<>`'ın ifade edemediği imzalar için (`ref`/`out` parametreler, 16'dan fazla parametre) gerçek bir
delegate tipi üretir. Aynı imza tekrar istenirse aynı tip döner.

```csharp
Type d = DynamicDelegateTypeFactory.GetOrCreate(
    new[] { typeof(string), typeof(int).MakeByRefType() },   // (string, out int)
    typeof(bool));
```

---

### DynamicClass — hepsi tek bir akışta

`DynamicTypeFactory` + `DynamicEntityAccessor` + `EvokerBuilder` üçlüsünü tek bir akıcı (fluent) nesnede toplar.
Thread-safe build sunar: aynı tanım birden fazla thread'den kurulsa bile tip ve instance yalnızca bir kez üretilir.

#### Oluşturma

```csharp
var dc = DynamicClass.CreateClass("Musteri");                    // şema cache'li, paylaşımlı
var tek = DynamicClass.CreateClass("Rapor", useSchemaCache: false, forgetOnDispose: true);  // tek kullanımlık şema
var sar = DynamicClass.Wrap(tip, mevcutNesne);                   // var olan bir nesneyi sarmala
```

#### Property'ler ve değerler

```csharp
dc.AddProperty<int>("Id").AddProperty("Ad", typeof(string));

dc.SetValue("Id", 1);                 // tipli, boxing yok
dc.SetValue<int>("Id", 1);
dc.SetValue("Id", (object)1);         // tip bilinmeden
int id      = dc.GetValue<int>("Id");
object? ham = dc.GetValue("Id");
```

#### Değişiklik kancaları (instance'a özel)

```csharp
dc.OnSet<int>("Id", (eski, yeni) => Console.WriteLine($"{eski} -> {yeni}"));
dc.OnGet<int>("Id", deger => Console.WriteLine($"okundu: {deger}"));
dc.RemoveOnSet("Id");
dc.RemoveOnGet("Id");
```

#### Metotlar

Metot gövdesi bir delegate'tir; istediğiniz an değiştirebilirsiniz.

```csharp
dc.AddMethod<Func<int, int, int>>("Topla");
dc.AddMethod("Logla", typeof(Action<string>));
dc.SetMethod<Func<int, int, int>>("Topla", (a, b) => a + b);
dc.SetMethod("Logla", (Action<string>)Console.WriteLine);

int  t  = dc.InvokeMethod<int>("Topla", 3, 4);
dc.InvokeMethod("Logla", "merhaba");
int  ta = await dc.InvokeMethodAsync<int>("ToplaAsync", 3, 4);
Type dt = dc.GetMethodDelegateType("Topla");                       // Func<int,int,int>
```

#### Generic metotlar (type-erasure)

```csharp
dc.AddGenericMethod(typeof(IDepo).GetMethod("Getir")!);            // T Getir<T>(string anahtar)
dc.SetGenericMethod("Getir", (tipArgs, args) => depo.Oku(tipArgs[0], (string)args[0]!));
```

#### Event'ler

```csharp
dc.AddEvent<EventHandler>("Degisti");
dc.AddEvent("Uyari", typeof(EventHandler<string>));
dc.RaiseEvent("Degisti", dc.RawInstance, EventArgs.Empty);
```

Gerçek C# `+=` söz dizimiyle abone olmak için tipi bir interface'e bağlayın:
[Extend → `Implement<T>`](https://github.com/DSOpenServer/DSO.Core.Evoker.Extend/blob/main/README.md).

#### Attribute ekleme

```csharp
dc.AddTypeAttribute<SerializableAttribute>();
dc.AddPropertyAttribute<ObsoleteAttribute>("EskiAlan", "Kullanmayın");
```

#### Yaşam döngüsü ve genişletme

```csharp
dc.WithTypeConfigurator((tb, members) => { /* ileri seviye IL */ });  // Extend'in kullandığı kanca
dc.Build();          // elle build (ilk kullanım zaten build eder)
dc.Warmup();         // property + metot cache'lerini önceden ısıt
Type   tip  = dc.Type;
object nes  = dc.RawInstance;
var    sema = dc.Schema;        // IReadOnlyList<(string Name, Type Type)>
dc.Dispose();        // forgetOnDispose: true ise cache'leri de temizler
```

> **Cache ömrü:** Üretilen tip ve derlenmiş accessor'lar paylaşımlı, process ömürlü cache'lere aittir. Her seferinde
> farklı kolon seti üreten senaryolarda `useSchemaCache: false, forgetOnDispose: true` kullanıp `using` ile temizleyin.

---

### Commands — JSON komutlar

`DSO.Core.Evoker.Commands` ad alanı, **herhangi bir sınıfı** kodunda hiçbir değişiklik yapmadan JSON ile kullanılabilir
hale getirir. Aynı komut biçimi her yerde çalışır:

- bir tipte (`EvokerTarget`),
- bir plugin'de ([`PluginTarget`](https://github.com/DSOpenServer/DSO.Core.Evoker.Plugins/blob/main/README.md), sandbox worker'ın içinde bile),
- web üzerinden ([DSO.Core.Evoker.Api](https://github.com/DSOpenServer/DSO.Core.Evoker.Api/blob/main/README.md)).

#### Komut biçimi

```jsonc
{ "op": "invoke", "member": "Add", "args": [3, 4] }                               // sıralı argüman
{ "op": "invoke", "member": "Greet", "args": { "name": "Ali" } }                  // isimli (eksik optional = varsayılan)
{ "op": "invoke", "member": "Pick", "args": [5], "argTypes": ["decimal"] }        // overload ipucu
{ "op": "get", "member": "BatchSize" }
{ "op": "set", "member": "BatchSize", "value": 500 }
{ "op": "batch", "member": "Price", "argsList": [[1, 10], [2, 5]] }               // aynı metot, çok argüman seti
{ "steps": [ { ... }, { ... } ], "stopOnError": true }                             // sıralı adımlar, aynı instance
{ "op": "invoke", "member": "YavasIs", "timeoutMs": 500 }                          // bekleme üst sınırı
{ "op": "invoke", "member": "X", "constructorArgs": { "baglanti": "..." } }        // Scoped/Transient için ctor argümanları
{ "op": "invoke", "member": "X", "as": "ilkAdim" }                                 // adım etiketi (sonuçta döner)
```

Alan adları büyük/küçük harf duyarsızdır. `op` verilmezse `invoke` kabul edilir.

**Argüman bağlama kuralları:**

- **Sıralı:** argüman sayısı ≤ parametre sayısı olmalı; kalan parametreler optional olmalı.
- **İsimli:** her ad bir parametreye karşılık gelmeli; verilmeyen parametreler optional olmalı.
- **Tek parametreli metot:** isimli nesne hiçbir adayla eşleşmezse, nesnenin kendisi o tek parametrenin değeri sayılır
  (`SaveCustomer(Customer c)`'ye doğrudan `{ "Code": "C1" }`).
- **Puanlama:** adaylar JSON değer türü uyumuna göre puanlanır. Tam sayı önce int, sonra long ve diğerleri; ondalık önce
  double, sonra decimal, sonra float. Değer tipe sığmalıdır (300 `byte`'a gitmez). Metin; string, tarih, Guid veya enum
  adı olabilir. Nesne, sınıf ya da dictionary olabilir.
- **params / ParamArray:** argümanlar tek tek de verilebilir (`["A", "B"]`); hiç verilmezse boş dizi geçer.
- **Belirsizlik:** eşit puanlı adaylar kalırsa `AmbiguousMatch` döner ve `argTypes` ile belirtmeniz istenir.

#### Komutları koddan üretmek

```csharp
var c1 = EvokerCommand.Parse(json);                       // ya da FromElement(jsonElement)
var c2 = EvokerCommand.Invoke("Add", 3, 4);
var c3 = EvokerCommand.InvokeNamed("Greet", new { name = "Ali" });
var c4 = EvokerCommand.Get("BatchSize");
var c5 = EvokerCommand.Set("BatchSize", 500);
var c6 = EvokerCommand.Batch("Price", new[] { new object?[] { 1, 10 }, new object?[] { 2, 5 } });
var c7 = EvokerCommand.Multi(c5, c2, c4);
string json = c7.ToJson(indented: true);
```

#### EvokerTarget ve nesne ömrü

`EvokerLifetime`, .NET'in DI ömürleriyle aynı mantıkta çalışır:

| Ömür | Davranış |
|---|---|
| `Singleton` (varsayılan) | Tek nesne; durum komutlar arasında korunur |
| `Scoped` | Komut başına bir nesne; çok adımlı komutun adımları aynı nesneyi paylaşır |
| `Transient` | Her adımda yeni nesne |
| `Static` | Nesne yok; static sınıflar otomatik olarak bu ömrü alır |

```csharp
var a = new EvokerTarget(typeof(FaturaServisi));                                            // Singleton
var b = new EvokerTarget(typeof(Repo), EvokerLifetime.Scoped, constructorArgs: new object[] { "Server=." });
var c = new EvokerTarget(typeof(Hesap), EvokerLifetime.Transient, includeNonPublic: true);
var d = new EvokerTarget(typeof(Kur));                                                      // static sınıf -> Static
var e = EvokerTarget.ForInstance(mevcutNesne);                                              // hazır nesne
var f = new EvokerTarget(typeof(Repo)).WithConstructorJson(JsonDocument.Parse("{\"baglanti\":\"...\"}").RootElement);

a.DefaultTimeoutMs = 2000;                    // komutta timeoutMs yoksa geçerli
EvokerCommandResult r = await a.ExecuteAsync(EvokerCommand.Invoke("Kes", "C001", 150m));
EvokerTypeDescriptor tanim = await a.DescribeAsync(new EvokerDescribeOptions { IncludeSamples = true, IncludeValues = true });
a.Reset();                                    // Singleton nesnesini bırak (sonraki komut yenisini oluşturur)

object nesne = EvokerTarget.CreateInstance(typeof(Repo), constructorJson);   // aynı kurallarla tek seferlik nesne
```

Özellikler: `Type`, `Lifetime`, `IncludeNonPublic`, `Kind` (`"Type"` / `"Instance"`), `TypeFullName`, `Instance` (Singleton
nesnesi). Kendi hedef türünüzü yazmak için `IEvokerTarget` arayüzünü (`Kind`, `TypeFullName`, `ExecuteAsync`,
`DescribeAsync`) uygulayın. Plugins paketindeki `PluginTarget` bu arayüzü kullanır.

#### EvokerCatalog — Guid anahtarlı hedef listesi

```csharp
var katalog = new EvokerCatalog();

Guid k1 = katalog.Register(typeof(FaturaServisi), name: "Fatura");                    // Singleton
Guid k2 = katalog.Register<Sepet>(EvokerLifetime.Scoped, name: "Sepet");
Guid k3 = katalog.RegisterInstance(appCache, name: "Uygulama cache'i");
Guid k4 = katalog.RegisterWithJsonConstructor(typeof(Repo), ctorJson, EvokerLifetime.Singleton);
Guid k5 = katalog.Register(ozelHedef /* IEvokerTarget */, name: "Özel", key: sabitGuid);

EvokerCommandResult r1 = await katalog.ExecuteAsync(k1, "{ \"member\": \"Kes\", \"args\": [\"C001\"] }");
EvokerCommandResult r2 = await katalog.ExecuteAsync(k1, EvokerCommand.Get("SonFaturaNo"));
EvokerTypeDescriptor t  = await katalog.DescribeAsync(k1, new EvokerDescribeOptions { IncludeSamples = true });

katalog.Rename(k1, "Fatura servisi");            // ad sadece açıklamadır; anahtar her zaman Guid
EvokerCatalogEntry? e = katalog.Find(k1);        // Key, Name, Target, Kind, TypeFullName, Lifetime, RegisteredUtc
IReadOnlyList<EvokerCatalogEntry> hepsi = katalog.Entries;
katalog.Unregister(k3);
```

**İsimle erişim ve izin listesi:** Web'e açılan senaryolar için kayıt gerektirmeyen tipler sadece izin verilen
kalıplarla açılır. Varsayılan **kapalıdır**.

```csharp
katalog.AllowTypesFrom("Acme.*", "System.Math");   // assembly / namespace / tam ad kalıpları; "*" = her şey (önerilmez)
bool ok = katalog.IsTypeAllowed(typeof(Math));
IReadOnlyList<Type> izinli = katalog.ListAllowedTypes(search: "Math", max: 200);
Type tip = katalog.ResolveAllowedType("System.Math");                        // izin yoksa NotAllowed
var r = await katalog.ExecuteOnTypeAsync("System.Math", EvokerCommand.Invoke("Max", 3, 7));   // 7 (int overload)
var d = await katalog.DescribeTypeAsync("System.Math", new EvokerDescribeOptions { IncludeSamples = true });
IReadOnlyList<string> kaliplar = katalog.AllowedPatterns;
```

#### Sonuç ve hata modeli

Komutlar **exception fırlatmaz**; hatalar sonucun içinde döner. Bu yüzden web'e doğrudan verilebilir.

```jsonc
{ "success": true, "result": 7, "elapsedMs": 0.08 }
{ "success": true, "steps": [ { "index": 0, "op": "set", "success": true, ... } ], "elapsedMs": 412.6 }
{ "success": false, "error": { "code": "TargetException", "message": "...", "exceptionType": "System.InvalidOperationException", "stepIndex": 1 } }
```

| Tip | Üyeler |
|---|---|
| `EvokerCommandResult` | `Success`, `Result`, `Steps`, `Error`, `ElapsedMs`, `Mode`, `ToJson(indented)`, `FromJson(json)`, `Fail(...)`, `Options` |
| `EvokerStepResult` | `Index`, `Op`, `Member`, `As`, `Success`, `Result`, `Error`, `ElapsedMs`, `Skipped` |
| `EvokerError` | `Code`, `Message`, `ExceptionType`, `StepIndex`, `BatchIndex`, `Member` |
| `EvokerCommandException` | `Code`, `TargetExceptionType`, `BatchIndex` (kod içinden fırlatmak için) |

| `EvokerErrorCodes` | HTTP (`HttpStatus(code)`) | Anlamı |
|---|---:|---|
| `BadRequest` | 400 | Komut okunamadı / geçersiz |
| `NotAllowed` | 403 | İzin listesi dışında |
| `TargetNotFound` | 404 | Hedef yok |
| `MemberNotFound` | 404 | Üye yok |
| `Inactive` | 409 | Hedef pasif (plugin) |
| `InvalidArguments` | 422 | Argümanlar hiçbir imzaya uymuyor |
| `AmbiguousMatch` | 422 | Birden fazla imza eşit uyuyor |
| `InvalidOperation` | 422 | İşlem bu hedefte yapılamaz |
| `Cancelled` | 499 | İstek iptal edildi |
| `TargetException` | 500 | Hedefin kendi kodu exception fırlattı |
| `InternalError` | 500 | Beklenmeyen hata |
| `Timeout` | 504 | `timeoutMs` aşıldı |

---

### Conversion — değer dönüştürme

`DSO.Core.Evoker.Conversion` ad alanı, tüm katmanların ortak dönüştürücüsüdür.

```csharp
object? v1 = EvokerValueConverter.ConvertTo(5L, typeof(int));               // sayısal genişletme / daraltma
object? v2 = EvokerValueConverter.ConvertTo("High", typeof(Seviye));         // enum adı
object? v3 = EvokerValueConverter.ConvertTo(pluginPoint, typeof(PointDto));  // farklı tip, aynı şekil -> eşleme
object? v4 = EvokerValueConverter.FromJson(jsonElement, typeof(Musteri));    // JSON -> tip (hoşgörülü)
object? v5 = EvokerValueConverter.Natural(jsonElement);                      // JSON -> doğal CLR değeri
int skor   = EvokerValueConverter.JsonScore(jsonElement, typeof(int));       // 0 = uymaz, yüksek = iyi uyum
bool yaprak = EvokerValueConverter.IsLeafType(typeof(DateTime));

JsonSerializerOptions girdi = EvokerJson.Input;    // büyük/küçük harf duyarsız, enum adı/sayısı, "12" -> 12, field'lar
JsonSerializerOptions cikti = EvokerJson.Output;   // Türkçe karakter kaçışsız, enum metin, döngü güvenli, NaN güvenli
JsonSerializerOptions sekil = EvokerJson.Shape;    // tip <-> tip şekil eşlemesi
JsonElement e = EvokerJson.ToElement(herhangiNesne);
```

`ConvertTo`, aynı şekle sahip iki farklı tip arasında (ör. plugin'in `Point`'i ile host'un `PointDto`'su) dönüşümü
dahili, derlenmiş bir kopyalayıcıyla yapar. Bu kopyalayıcı sonucu JSON yoluyla birebir aynı olan şekillerde kullanılır;
diğer durumlarda JSON'a düşülür.

**Unload kapıları:** `EvokerValueConverter.ForgetAssembly(asm)` ve `EvokerJson.ForgetAssembly(asm)` çağrıları,
System.Text.Json'ın global cache'leri dahil, bir assembly'ye ait tüm metadata'yı bırakır.

---

### Description — tip tanımı ve şablonlar

`DSO.Core.Evoker.Description` ad alanı, bir tipin tam tanımını ve her üye için hazır JSON komut şablonlarını üretir. Hem
çalışan tiplerle hem de **DLL'i çalıştırmadan** (MetadataLoadContext tipleriyle) çalışır.

```csharp
EvokerTypeDescriptor d = EvokerDescriber.Describe(typeof(FaturaServisi), new EvokerDescribeOptions
{
    IncludeNonPublic   = true,    // private/protected/internal da listelensin
    IncludeInherited   = true,    // kendi assembly'sindeki taban sınıf üyeleri (DeclaredIn ile işaretli)
    IncludeSamples     = true,    // her üye için hazır komut şablonu
    IncludeValues      = false,   // o anki değerler (getter'lar ÇALIŞIR)
    MaxValueJsonLength = 4096
});
string json = d.ToJson();

string ad = EvokerDescriber.Friendly(typeof(Dictionary<string, List<int>>));   // "Dictionary<string, List<int>>"
await EvokerDescriber.CaptureValuesAsync(d,
    read: (uye, alanMi) => okuyucuAsync(uye, alanMi),   // Func<string, bool, Task<object?>>: üye adı, field mı -> değer
    includeNonPublic: false, source: "InProcess", maxValueJsonLength: 4096, readStatic: false);

JsonElement komut = EvokerSampleBuilder.Command("invoke", "Kes", args);
JsonElement args2 = EvokerSampleBuilder.ArgsObject(metot.GetParameters());
JsonElement iskelet = EvokerSampleBuilder.Skeleton(typeof(Musteri));            // { "Kod": "", "Adres": { ... } }
```

Tanım modeli şunları içerir:

- **`EvokerTypeDescriptor`:** `FullName`, `Name`, `Namespace`, `AssemblyName`, `Kind`, `BaseType`, `Interfaces`,
  `Constructors`, `Methods`, `Properties`, `Fields`, `Events`, `Values`, `Warnings`.
- **Metot tanımı:**
  - adı, görünürlüğü ve dönüş tipi;
  - parametreleri (yön, optional, varsayılan değer, `params`);
  - `IsStatic`, `IsAsync`, `IsVirtual`, `IsAbstract`, `IsOverride` bayrakları ve generic argümanlar;
  - `DeclaredIn`;
  - okunur `Signature` (`"Task<int> AddAsync(int a, int b = 5)"`);
  - çağrılamıyorsa nedeni (`NotCallableReason`);
  - hazır `Sample` komutu.
- **Property tanımı:**
  - get ve set için ayrı görünürlük (ör. `Getter=Public`, `Setter=Private`);
  - `init`, static ve indexer bilgisi;
  - o anki `Value`;
  - okuma ve yazma şablonları (`Sample`, `SampleSet`).
- **`EvokerTypeKind`:** `Class`, `StaticClass`, `AbstractClass`, `Record`, `Struct`, `Enum`, `Interface`, `Delegate`.

---

## Plugin ve unload dostu tasarım

Collectible `AssemblyLoadContext`'ler, içlerindeki bir tipe güçlü referans tutan **tek bir statik cache** yüzünden bile
bellekten atılamaz. Evoker'daki her cache'in bu yüzden bir çıkış kapısı vardır:

| Cache | Temizleme |
|---|---|
| Derlenmiş metot delegate'leri | `EvokerBuilder.ForgetType(type)` |
| Accessor ve constructor'lar | `DynamicEntityAccessor.ForgetType(type)` / `builder.ForgetCache()` |
| Komut üye cache'i | `EvokerBuilder.ForgetType` ile birlikte otomatik |
| Tip adı cache'i | Collectible tipler zaten zayıf referanslı; context unload olunca kendiliğinden silinir |
| JSON / şekil eşleme | `EvokerJson.ForgetAssembly(asm)`, `EvokerValueConverter.ForgetAssembly(asm)` |

[DSO.Core.Evoker.Plugins](https://github.com/DSOpenServer/DSO.Core.Evoker.Plugins/blob/main/README.md) bunların hepsini unload sırasında kendisi çağırır.
`UnloadTest` paketi, in-process bir plugin'in Evoker ile defalarca kullanıldıktan sonra **gerçekten** bellekten
atıldığını doğrular.

---

## Testler ve örnekler

| Proje | İçerik |
|---|---|
| `DSO.Core.Evoker.TestRunner` | Çekirdek + Extend test koşucusu (**228 kontrol**). Bölümler: `BuilderFeatureTests` (F1–F8: optional, VB büyük/küçük harf, event, cache temizliği, ResolveType, negatif cache, tipli delegate, hızlı yol), `CommandTests` (C1–C13: sıralı/isimli argüman, async, get/set, private, hatalar, çok adımlı/batch, ömürler, constructor seçimi, katalog ve izin listesi, tanım/şablon, sonuç JSON'u, params, sayısal overload), `ExtensionPointTests` (E1–E4), Extend testleri |
| `TestKit/PerfBench` | Bu sayfadaki tüm performans rakamlarının kaynağı (`dotnet run -c Release -- <SamplePlugin.dll> <PluginHost.dll>`) |
| `DSO.Core.Evoker.Plugins.DemoApi` | Evoker katalog hedeflerinin (Singleton/Scoped/Transient/Static) web üzerinden kullanımı — `demo.http` |

Çalıştırma:

```
dotnet run --project DSO.Core.Evoker.TestRunner
```

Beklenen çıktı: her bölüm `TÜM ... TESTLERİ GEÇTİ ✅` ile biter.

---

## Bilinen sınırlar

- Şema ve accessor cache üst sınırları **FIFO** tahliye kullanır; gerçek LRU değildir.
- `ForgetType`/`ForgetSchema` kendi cache'lerimizi temizler. `DynamicTypeFactory`'nin ürettiği tipler collectible değildir
  (`AssemblyBuilderAccess.Run`); metadata'ları process ömrü boyunca kalır.
- `EvokerBuilder` ile `ref`/`out` parametreli metot çağrılmaz; bunun yerine açık bir hata verilir.
- Üretilen tiplerin `ToString`/`Equals`/`GetHashCode` davranışı `object`'ten gelir.

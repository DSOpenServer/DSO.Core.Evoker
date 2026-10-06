using DSO.Core.Evoker;
using DSO.Core.Evoker.Commands;
using DSO.Core.Evoker.Description;
using DSO.Core.Evoker.TestApi;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json;

namespace DSO.Core.Evoker.TestApi
{
    public class TestClass1
    {
        public string Run(int data, string str)
        {
            return $"Test Class Run edildi.{data.ToString()} - {str}";
        }
        public int Run2() { return 9; }
    }

    public class TestClass2
    {
        public string propString;
        public int propInt;
        public TestClass2(string prp, int prpInt)
        {
            propString = prp;
            propInt = prpInt;
        }
        public string Run(int data, string str)
        {
            return $"Test Class2 Contractor:{propString},{propInt} - Run edildi: {data.ToString()} - {str}";
        }
    }

    public class TestClass3
    {
        public static string Run()
        {
            return $"Test Class3 Run edildi. - Contractor Yok ve method yok";
        }
        public static int Run2() { return 8; }
        public static void Run3()
        {
            Console.WriteLine("Burası Run3");
        }
    }

    public static class EvokerTests
    {
        public static async Task RunAllAsync()
        {
            int failures = 0;
            void Check(string name, bool condition, string detail = "")
            {
                if (condition)
                {
                    Console.WriteLine($"  [OK]   {name}");
                }
                else
                {
                    Console.WriteLine($"  [FAIL] {name}  {detail}");
                    failures++;
                }
            }

            Console.WriteLine("=== TEST 1: TestClass1 (parametresiz new, iki farklı method) ===");
            {
                var run = EvokerEngine.InvokePublic<TestClass1, string>("Run", 5, "text alanı");
                var run2 = EvokerEngine.InvokePublic<TestClass1, int>("Run2");
                Check("Run() beklenen metni döndü", run == "Test Class Run edildi.5 - text alanı", $"got={run}");
                Check("Run2() == 9", run2 == 9, $"got={run2}");
            }

            Console.WriteLine("=== TEST 2: TestClass2 (constructor argümanlı) - AYNI TIP İKİ FARKLI CTOR ARGÜMANIYLA ===");
            {
                var runA = EvokerEngine.Target<TestClass2>("CTOR-A", 1)
                    .Invoke<string>("Run", 6, "birinci-cagri");
                var runB = EvokerEngine.Target<TestClass2>("CTOR-B", 2)
                    .Invoke<string>("Run", 7, "ikinci-cagri");

                Console.WriteLine($"  runA = {runA}");
                Console.WriteLine($"  runB = {runB}");

                Check("runA CTOR-A içeriyor", runA != null && runA.Contains("CTOR-A"), $"got={runA}");
                Check("runB CTOR-B içeriyor (BUG VARSA BURASI FAILS OLUR: runA ile aynı çıkar)",
                    runB != null && runB.Contains("CTOR-B"), $"got={runB}");
            }

            Console.WriteLine("=== TEST 3: TestClass3 (static) ===");
            {
                var run = EvokerEngine.InvokePublic<TestClass3, string>("Run");
                var run2 = EvokerEngine.InvokePublic<TestClass3, int>("Run2");
                EvokerEngine.ExecutePublic<TestClass3>("Run3");
                Check("Run() statik metod doğru", run != null && run.Contains("Contractor Yok"), $"got={run}");
                Check("Run2() == 8", run2 == 8, $"got={run2}");
            }

            Console.WriteLine("=== TEST 4: DynamicTypeFactory - AYNI İSİMLE İKİ FARKLI DINAMIK TIP/INSTANCE ===");
            {
                var props = new Dictionary<string, Type> { { "Id", typeof(int) }, { "Name", typeof(string) } };

                Type dynType1 = DynamicTypeFactory.CreateType("DynamicCustomer", props);
                object instance1 = Activator.CreateInstance(dynType1)!;
                dynType1.GetProperty("Name")!.SetValue(instance1, "Musteri-BIR");

                Type dynType2 = DynamicTypeFactory.CreateType("DynamicCustomer", props); // AYNI isim, FARKLI Type!
                object instance2 = Activator.CreateInstance(dynType2)!;
                dynType2.GetProperty("Name")!.SetValue(instance2, "Musteri-IKI");

                var name1 = new EvokerBuilder(dynType1).SetInstance(instance1).Invoke<string>("get_Name");
                var name2 = new EvokerBuilder(dynType2).SetInstance(instance2).Invoke<string>("get_Name");

                Console.WriteLine($"  name1 = {name1}");
                Console.WriteLine($"  name2 = {name2}");

                Check("name1 == Musteri-BIR", name1 == "Musteri-BIR", $"got={name1}");
                Check("name2 == Musteri-IKI (BUG VARSA BURASI FAILS OLUR: name1 ile aynı çıkar)",
                    name2 == "Musteri-IKI", $"got={name2}");
            }

            Console.WriteLine("=== TEST 5: Aynı tip üzerinde SetInstance ile İKİ FARKLI nesne, ARDIŞIK çağrılar ===");
            {
                var obj1 = new TestClass2("Instance-1", 100);
                var obj2 = new TestClass2("Instance-2", 200);

                var r1 = new EvokerBuilder(typeof(TestClass2)).SetInstance(obj1).Invoke<string>("Run", 1, "x");
                var r2 = new EvokerBuilder(typeof(TestClass2)).SetInstance(obj2).Invoke<string>("Run", 2, "y");

                Console.WriteLine($"  r1 = {r1}");
                Console.WriteLine($"  r2 = {r2}");

                Check("r1 Instance-1 içeriyor", r1 != null && r1.Contains("Instance-1"), $"got={r1}");
                Check("r2 Instance-2 içeriyor (BUG VARSA r1 ile aynı çıkar)", r2 != null && r2.Contains("Instance-2"), $"got={r2}");
            }

            Console.WriteLine("=== TEST 6: EŞZAMANLI (concurrent) çağrılar - 200 paralel istek, karışık instance'lar ===");
            {
                int n = 200;
                var results = new string?[n];
                Parallel.For(0, n, i =>
                {
                    var obj = new TestClass2($"Paralel-{i}", i);
                    results[i] = new EvokerBuilder(typeof(TestClass2)).SetInstance(obj).Invoke<string>("Run", i, "z");
                });

                bool allCorrect = true;
                for (int i = 0; i < n; i++)
                {
                    if (results[i] == null || !results[i]!.Contains($"Paralel-{i},{i}") || !results[i]!.Contains($"{i} - z"))
                    {
                        allCorrect = false;
                        Console.WriteLine($"  Uyuşmazlık: index={i} got={results[i]}");
                    }
                }
                Check("200 paralel çağrının hepsi kendi instance verisini döndü", allCorrect);
            }

            Console.WriteLine("=== TEST 7: Overload çözümlemesi - aynı isim, aynı parametre sayısı, farklı tipler ===");
            {
                var r1 = EvokerEngine.InvokePublic<OverloadTest, string>("Calc", 5, 3);       // (int,int)
                var r2 = EvokerEngine.InvokePublic<OverloadTest, string>("Calc", "a", "b");   // (string,string)
                Check("Calc(int,int) doğru overload'a gitti", r1 == "int:8", $"got={r1}");
                Check("Calc(string,string) doğru overload'a gitti", r2 == "string:ab", $"got={r2}");
            }

            Console.WriteLine("=== TEST 8: Async metodlar (InvokeAsync/ExecuteAsync) + instance izolasyonu ===");
            {
                var a1 = new AsyncTest("Async-1");
                var a2 = new AsyncTest("Async-2");

                var t1 = new EvokerBuilder(typeof(AsyncTest)).SetInstance(a1).InvokeAsync<string>("GetNameAsync");
                var t2 = new EvokerBuilder(typeof(AsyncTest)).SetInstance(a2).InvokeAsync<string>("GetNameAsync");

                var r1 = await t1;
                var r2 = await t2;

                Check("Async çağrı 1 doğru instance'ı döndü", r1 == "Async-1", $"got={r1}");
                Check("Async çağrı 2 doğru instance'ı döndü", r2 == "Async-2", $"got={r2}");
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "TÜM TESTLER GEÇTİ ✅" : $"{failures} TEST BAŞARISIZ ❌");
        }

        // Test verisi için yardımcı sınıflar (bilerek bu dosyanın içinde, private/nested değil,
        // ama namespace'i kirletmemesi için EvokerTests ile aynı dosyada tutuluyor).
        private class AsyncTest
        {
            private readonly string _name;
            public AsyncTest(string name) { _name = name; }
            public async Task<string> GetNameAsync()
            {
                await Task.Delay(5);
                return _name;
            }
        }

        private class OverloadTest
        {
            public string Calc(int a, int b) => $"int:{a + b}";
            public string Calc(string a, string b) => $"string:{a}{b}";
        }
    }

    public static class PerformanceTests
    {
        public static void RunAllAsync()
        {
            int failures = 0;
            void Check(string name, bool condition, string detail = "")
            {
                if (condition) Console.WriteLine($"  [OK]   {name}");
                else { Console.WriteLine($"  [FAIL] {name}  {detail}"); failures++; }
            }

            var props = new Dictionary<string, Type> { { "Id", typeof(int) }, { "Name", typeof(string) } };

            Console.WriteLine("=== TEST P1: Şema cache - aynı şema AYNI Type'ı döndürmeli ===");
            {
                Type t1 = DynamicTypeFactory.CreateType("Customer", props);
                Type t2 = DynamicTypeFactory.CreateType("Customer", props); // aynı şema, ikinci çağrı
                Check("Aynı şema aynı Type nesnesini döndürdü (cache çalışıyor)", ReferenceEquals(t1, t2), $"t1={t1.Name}, t2={t2.Name}");

                // Farklı şema (farklı property seti) -> farklı Type olmalı, aynı "Customer" adıyla bile
                var propsV2 = new Dictionary<string, Type> { { "Id", typeof(int) }, { "Name", typeof(string) }, { "Email", typeof(string) } };
                Type t3 = DynamicTypeFactory.CreateType("Customer", propsV2);
                Check("Farklı şema farklı Type üretti (isim çakışmasına rağmen)", !ReferenceEquals(t1, t3));
            }

            Console.WriteLine("=== TEST P2: CreateUniqueType her seferinde YENİ Type üretmeli ===");
            {
                Type u1 = DynamicTypeFactory.CreateUniqueType("Widget", props);
                Type u2 = DynamicTypeFactory.CreateUniqueType("Widget", props);
                Check("CreateUniqueType iki farklı Type üretti", !ReferenceEquals(u1, u2));
            }

            Console.WriteLine("=== TEST P3: DynamicEntityAccessor - constructor + tipe özel getter/setter doğruluğu ===");
            {
                Type custType = DynamicTypeFactory.CreateType("Customer", props);

                var ctor = DynamicEntityAccessor.GetConstructor(custType);
                object instance = ctor();

                var setId = DynamicEntityAccessor.GetSetter<int>(custType, "Id");
                var setName = DynamicEntityAccessor.GetSetter<string>(custType, "Name");
                var getId = DynamicEntityAccessor.GetGetter<int>(custType, "Id");
                var getName = DynamicEntityAccessor.GetGetter<string>(custType, "Name");

                setId(instance, 42);
                setName(instance, "Dokuz Sistem");

                Check("Id doğru okundu", getId(instance) == 42, $"got={getId(instance)}");
                Check("Name doğru okundu", getName(instance) == "Dokuz Sistem", $"got={getName(instance)}");

                // İkinci bir instance ile cache'in instance'lar arasında karışmadığını doğrula
                object instance2 = ctor();
                setId(instance2, 7);
                setName(instance2, "İkinci Müşteri");
                Check("İki farklı instance birbirine karışmadı", getId(instance) == 42 && getId(instance2) == 7,
                    $"i1.Id={getId(instance)}, i2.Id={getId(instance2)}");
            }

            Console.WriteLine("=== TEST P4: Warmup - önceden derleme çalışıyor mu ===");
            {
                Type orderType = DynamicTypeFactory.CreateType("Order", new Dictionary<string, Type>
                {
                    { "Id", typeof(int) }, { "Total", typeof(decimal) }
                });

                int before = DynamicEntityAccessor.CachedConstructorCount;

                DynamicEntityAccessor.Warmup(new[]
                {
                    (orderType, (IEnumerable<(string, Type)>)new[] { ("Id", typeof(int)), ("Total", typeof(decimal)) })
                });

                int after = DynamicEntityAccessor.CachedConstructorCount;
                Check("Warmup constructor cache'e ekledi", after > before, $"before={before}, after={after}");

                // Warmup sonrası ilk gerçek kullanım artık "cache miss" olmamalı (fonksiyonel: hata vermeden çalışmalı)
                var getTotal = DynamicEntityAccessor.GetGetter<decimal>(orderType, "Total");
                var setTotal = DynamicEntityAccessor.GetSetter<decimal>(orderType, "Total");
                var ctor = DynamicEntityAccessor.GetConstructor(orderType);
                var inst = ctor();
                setTotal(inst, 99.5m);
                Check("Warmup sonrası accessor doğru çalıştı", getTotal(inst) == 99.5m, $"got={getTotal(inst)}");
            }

            Console.WriteLine("=== TEST P5: Bounded cache (FIFO tahliye) çalışıyor mu ===");
            {
                int oldMax = DynamicEntityAccessor.MaxAccessorCacheSize;
                try
                {
                    DynamicEntityAccessor.MaxAccessorCacheSize = 3;

                    Type t = DynamicTypeFactory.CreateType("BoundedTest", new Dictionary<string, Type>
                    {
                        { "A", typeof(int) }, { "B", typeof(int) }, { "C", typeof(int) }, { "D", typeof(int) }, { "E", typeof(int) }
                    });

                    // 5 farklı property için getter iste -> toplamda cache boyutu sınırı aşacak
                    DynamicEntityAccessor.GetGetter<int>(t, "A");
                    DynamicEntityAccessor.GetGetter<int>(t, "B");
                    DynamicEntityAccessor.GetGetter<int>(t, "C");
                    DynamicEntityAccessor.GetGetter<int>(t, "D");
                    DynamicEntityAccessor.GetGetter<int>(t, "E");

                    Check("Accessor cache boyutu sınırı aştı ama en fazla sınır kadar TUTULDU (FIFO tahliye çalıştı)",
                        DynamicEntityAccessor.CachedAccessorCount <= 3,
                        $"count={DynamicEntityAccessor.CachedAccessorCount}");

                    // Tahliyeden sonra bile yeniden istenirse yeniden derlenip ÇALIŞMAYA devam etmeli (hata fırlatmamalı)
                    var getterA = DynamicEntityAccessor.GetGetter<int>(t, "A");
                    var setterA = DynamicEntityAccessor.GetSetter<int>(t, "A");
                    var ctor = DynamicEntityAccessor.GetConstructor(t);
                    var inst = ctor();
                    setterA(inst, 123);
                    Check("Tahliye sonrası yeniden istenen accessor doğru çalışıyor", getterA(inst) == 123, $"got={getterA(inst)}");
                }
                finally
                {
                    DynamicEntityAccessor.MaxAccessorCacheSize = oldMax; // diğer testleri etkilememesi için sınırı geri al
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== BENCHMARK: Eski yöntem (Activator + EvokerBuilder/boxing) vs Yeni yöntem (typed accessor) ===");
            RunBenchmark();

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "TÜM PERFORMANS TESTLERİ GEÇTİ ✅" : $"{failures} TEST BAŞARISIZ ❌");
        }

        private static void RunBenchmark()
        {
            const int N = 200_000;

            var props = new Dictionary<string, Type> { { "Id", typeof(int) }, { "Amount", typeof(decimal) }, { "Name", typeof(string) } };
            Type benchType = DynamicTypeFactory.CreateType("BenchEntity", props);

            // --- Isınma turu (JIT/derleme maliyetini ölçümün dışında tutmak için) ---
            {
                var warm = Activator.CreateInstance(benchType)!;
                new EvokerBuilder(benchType).SetInstance(warm).Execute("set_Id", 1);
                var wCtor = DynamicEntityAccessor.GetConstructor(benchType);
                var wInst = wCtor();
                DynamicEntityAccessor.GetSetter<int>(benchType, "Id")(wInst, 1);
                DynamicEntityAccessor.GetSetter<decimal>(benchType, "Amount")(wInst, 1.5m);
                DynamicEntityAccessor.GetSetter<string>(benchType, "Name")(wInst, "x");
                _ = DynamicEntityAccessor.GetGetter<int>(benchType, "Id")(wInst);
            }

            // --- ESKİ YÖNTEM: Activator.CreateInstance + EvokerBuilder (Execute/Invoke, object[]+boxing) ---
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocBeforeOld = GC.GetAllocatedBytesForCurrentThread();
            var swOld = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                object obj = Activator.CreateInstance(benchType)!;
                var builder = new EvokerBuilder(benchType).SetInstance(obj);
                builder.Execute("set_Id", i);
                builder.Execute("set_Amount", 1.5m);
                builder.Execute("set_Name", "x");
                int id = builder.Invoke<int>("get_Id");
                _ = id;
            }
            swOld.Stop();
            long allocAfterOld = GC.GetAllocatedBytesForCurrentThread();
            long allocOld = allocAfterOld - allocBeforeOld;

            // --- YENİ YÖNTEM: GetConstructor + tipe özel GetGetter/GetSetter (boxing'siz) ---
            var ctorFast = DynamicEntityAccessor.GetConstructor(benchType);
            var setId = DynamicEntityAccessor.GetSetter<int>(benchType, "Id");
            var setAmount = DynamicEntityAccessor.GetSetter<decimal>(benchType, "Amount");
            var setName = DynamicEntityAccessor.GetSetter<string>(benchType, "Name");
            var getId = DynamicEntityAccessor.GetGetter<int>(benchType, "Id");

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocBeforeNew = GC.GetAllocatedBytesForCurrentThread();
            var swNew = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                object obj = ctorFast();
                setId(obj, i);
                setAmount(obj, 1.5m);
                setName(obj, "x");
                int id = getId(obj);
                _ = id;
            }
            swNew.Stop();
            long allocAfterNew = GC.GetAllocatedBytesForCurrentThread();
            long allocNew = allocAfterNew - allocBeforeNew;

            Console.WriteLine($"  N = {N:N0} satır, her satırda: 1 nesne + 3 set + 1 get");
            Console.WriteLine($"  ESKİ (Activator+EvokerBuilder) : {swOld.ElapsedMilliseconds,6} ms   |  alloc: {allocOld / 1024.0 / 1024.0,8:F2} MB   |  alloc/satır: {allocOld / (double)N,7:F1} B");
            Console.WriteLine($"  YENİ (typed accessor)          : {swNew.ElapsedMilliseconds,6} ms   |  alloc: {allocNew / 1024.0 / 1024.0,8:F2} MB   |  alloc/satır: {allocNew / (double)N,7:F1} B");

            if (allocOld > 0)
            {
                double speedup = swOld.ElapsedMilliseconds / Math.Max(1.0, swNew.ElapsedMilliseconds);
                double allocReduction = 100.0 * (1.0 - (double)allocNew / allocOld);
                Console.WriteLine($"  Hızlanma: ~{speedup:F1}x   |   Alloc azalması: ~%{allocReduction:F0}");
            }
        }
    }

    public static class DynamicClassTests
    {
        public static void RunAll()
        {
            int failures = 0;
            void Check(string name, bool condition, string detail = "")
            {
                if (condition) Console.WriteLine($"  [OK]   {name}");
                else { Console.WriteLine($"  [FAIL] {name}  {detail}"); failures++; }
            }

            Console.WriteLine("=== TEST D1: Orijinal Test4 örneği - yeni fluent API ile ===");
            {
                var yeniClass = DynamicClass.CreateClass("DynamicCustomer")
                    .AddProperty("Id", typeof(long))
                    .AddProperty("Name", typeof(string));

                yeniClass.SetValue<long>("Id", 9);
                yeniClass.SetValue<string>("Name", "Dokuz Sistem");

                var id = yeniClass.GetValue<long>("Id");
                var name = yeniClass.GetValue<string>("Name");

                Console.WriteLine($"  Id={id}, Name={name}");
                Check("Id doğru", id == 9, $"got={id}");
                Check("Name doğru", name == "Dokuz Sistem", $"got={name}");
            }

            Console.WriteLine("=== TEST D2: Zincirleme (fluent chaining) SetValue ===");
            {
                var yeniClass = DynamicClass.CreateClass("ChainTest")
                    .AddProperty<int>("A")
                    .AddProperty<int>("B");

                yeniClass.SetValue("A", 1).SetValue("B", 2); // SetValue zincirlenebiliyor mu?

                Check("Zincirleme SetValue çalıştı", yeniClass.GetValue<int>("A") == 1 && yeniClass.GetValue<int>("B") == 2);
            }

            Console.WriteLine("=== TEST D3: AddProperty ile ilgili YANLIŞ kullanım - Build sonrası AddProperty ENGELLENMELİ ===");
            {
                var yeniClass = DynamicClass.CreateClass("LockTest").AddProperty<int>("X");
                yeniClass.SetValue("X", 1); // artık build edildi

                bool threw = false;
                try { yeniClass.AddProperty<int>("Y"); }
                catch (InvalidOperationException) { threw = true; }

                Check("Build sonrası AddProperty InvalidOperationException fırlattı", threw);
            }

            Console.WriteLine("=== TEST D4: forgetOnDispose + useSchemaCache:true KOMBİNASYONU ENGELLENMELİ ===");
            {
                bool threw = false;
                try { DynamicClass.CreateClass("BadCombo", useSchemaCache: true, forgetOnDispose: true); }
                catch (ArgumentException) { threw = true; }

                Check("Geçersiz kombinasyon (forgetOnDispose+useSchemaCache) ArgumentException fırlattı", threw);
            }

            Console.WriteLine("=== TEST D5: Paylaşımlı cache - AYNI şemadan 1000 DynamicClass, accessor cache SADECE 1 KEZ büyümeli ===");
            {
                using var warm = MakeSchema("SharedSchema", 0);

                int before = DynamicEntityAccessor.CachedAccessorCount;

                var instances = new List<DynamicClass>();
                for (int i = 0; i < 1000; i++)
                {
                    instances.Add(MakeSchema("SharedSchema", i));
                }

                int after = DynamicEntityAccessor.CachedAccessorCount;

                Check("1000 aynı-şema örneği accessor cache'i BÜYÜTMEDİ (paylaşım çalışıyor)",
                    after == before, $"before={before}, after={after}");

                Check("Ama her örnek KENDİ değerini doğru tutuyor (instance karışması yok)",
                    instances[7].GetValue<int>("Id") == 7 && instances[999].GetValue<int>("Id") == 999);

                foreach (var inst in instances) inst.Dispose();
            }

            Console.WriteLine("=== TEST D6: İzole (useSchemaCache:false) + forgetOnDispose:true - gerçekten temizliyor mu ===");
            {
                int beforeCtor = DynamicEntityAccessor.CachedConstructorCount;
                int beforeAcc = DynamicEntityAccessor.CachedAccessorCount;

                using (var yeniClass = DynamicClass.CreateClass("Ephemeral", useSchemaCache: false, forgetOnDispose: true))
                {
                    yeniClass.AddProperty<int>("Z");
                    yeniClass.SetValue("Z", 5);
                    Check("Dispose ÖNCESİ değer doğru okunuyor", yeniClass.GetValue<int>("Z") == 5);
                }

                int afterCtor = DynamicEntityAccessor.CachedConstructorCount;
                int afterAcc = DynamicEntityAccessor.CachedAccessorCount;

                Check("Dispose sonrası constructor cache'i geri düştü (temizlendi)",
                    afterCtor <= beforeCtor, $"before={beforeCtor}, after={afterCtor}");
                Check("Dispose sonrası accessor cache'i geri düştü (temizlendi)",
                    afterAcc <= beforeAcc, $"before={beforeAcc}, after={afterAcc}");
            }

            Console.WriteLine("=== TEST D7: İzole tipler GERÇEKTEN farklı Type nesneleri mi ===");
            {
                using var e1 = DynamicClass.CreateClass("IzoleTest", useSchemaCache: false, forgetOnDispose: true);
                e1.AddProperty<int>("V");
                e1.SetValue("V", 1);

                using var e2 = DynamicClass.CreateClass("IzoleTest", useSchemaCache: false, forgetOnDispose: true);
                e2.AddProperty<int>("V");
                e2.SetValue("V", 2);

                Check("useSchemaCache:false ile her CreateClass FARKLI bir Type üretti", !ReferenceEquals(e1.Type, e2.Type));
                Check("Her instance kendi değerini koruyor", e1.GetValue<int>("V") == 1 && e2.GetValue<int>("V") == 2);
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "TÜM DynamicClass TESTLERİ GEÇTİ ✅" : $"{failures} TEST BAŞARISIZ ❌");
        }

        private static DynamicClass MakeSchema(string name, int id)
        {
            var dc = DynamicClass.CreateClass(name) // useSchemaCache: true (varsayılan)
                .AddProperty<int>("Id")
                .AddProperty<string>("Name");
            dc.SetValue("Id", id);
            dc.SetValue("Name", $"Item-{id}");
            return dc;
        }
    }

    public static class NewFeaturesTests
    {
        public static void RunAll()
        {
            int failures = 0;
            void Check(string name, bool condition, string detail = "")
            {
                if (condition) Console.WriteLine($"  [OK]   {name}");
                else { Console.WriteLine($"  [FAIL] {name}  {detail}"); failures++; }
            }

            Console.WriteLine("=== TEST N1: Madde 1 - Şema sorgulama (IsDynamicType / GetSchema) ===");
            {
                var dc = DynamicClass.CreateClass("SchemaTest")
                    .AddProperty<int>("Id")
                    .AddProperty<string>("Name");
                dc.SetValue("Id", 1);

                Check("IsDynamicType true döndü", DynamicTypeFactory.IsDynamicType(dc.Type));
                Check("IsDynamicType normal bir tip için false", !DynamicTypeFactory.IsDynamicType(typeof(string)));

                var schema = DynamicTypeFactory.GetSchema(dc.Type);
                Check("Schema null değil", schema != null);
                Check("Schema 2 property içeriyor", schema!.Count == 2, $"count={schema.Count}");
                Check("Schema Id:int içeriyor", schema.Any(p => p.Name == "Id" && p.Type == typeof(int)));
                Check("Schema Name:string içeriyor", schema.Any(p => p.Name == "Name" && p.Type == typeof(string)));

                var dcSchema = dc.Schema;
                Check("DynamicClass.Schema property de aynı sonucu veriyor", dcSchema.Count == 2);
            }

            Console.WriteLine("=== TEST N2: Madde 2 - OnSet hook (eski/yeni değer) çalışıyor mu ===");
            {
                var dc = DynamicClass.CreateClass("HookTest")
                    .AddProperty<int>("Score");

                int capturedOld = -1, capturedNew = -1;
                int callCount = 0;

                dc.OnSet<int>("Score", (oldV, newV) =>
                {
                    capturedOld = oldV;
                    capturedNew = newV;
                    callCount++;
                });

                dc.SetValue("Score", 10);
                Check("İlk set'te oldValue=0", capturedOld == 0, $"got={capturedOld}");
                Check("İlk set'te newValue=10", capturedNew == 10, $"got={capturedNew}");

                dc.SetValue("Score", 25);
                Check("İkinci set'te oldValue=10 (bir önceki değer)", capturedOld == 10, $"got={capturedOld}");
                Check("İkinci set'te newValue=25", capturedNew == 25, $"got={capturedNew}");
                Check("Hook toplam 2 kez çağrıldı", callCount == 2, $"got={callCount}");
            }

            Console.WriteLine("=== TEST N3: Madde 2 - Hook INSTANCE'A ÖZEL mi ===");
            {
                var dcHooked = DynamicClass.CreateClass("SharedHookTest").AddProperty<int>("X");
                var dcPlain = DynamicClass.CreateClass("SharedHookTest").AddProperty<int>("X");

                Check("İkisi aynı Type'ı paylaşıyor (şema cache doğrulaması)", ReferenceEquals(dcHooked.Type, dcPlain.Type));

                int hookFireCount = 0;
                dcHooked.OnSet<int>("X", (o, n) => hookFireCount++);

                dcHooked.SetValue("X", 1);
                dcPlain.SetValue("X", 999);

                Check("Hook'lu instance'ta hook 1 kez çalıştı", hookFireCount == 1, $"got={hookFireCount}");
                Check("Hook'suz instance etkilenmedi (hookFireCount hâlâ 1)", hookFireCount == 1, $"got={hookFireCount}");
                Check("Hook'suz instance kendi değerini doğru tutuyor", dcPlain.GetValue<int>("X") == 999);
            }

            Console.WriteLine("=== TEST N4: Madde 2 - OnGet hook ===");
            {
                var dc = DynamicClass.CreateClass("OnGetTest").AddProperty<string>("Name");
                dc.SetValue("Name", "Dokuz Sistem");

                int readCount = 0;
                string? lastRead = null;
                dc.OnGet<string>("Name", v => { readCount++; lastRead = v; });

                var v1 = dc.GetValue<string>("Name");
                var v2 = dc.GetValue<string>("Name");

                Check("OnGet 2 kez tetiklendi", readCount == 2, $"got={readCount}");
                Check("OnGet doğru değeri yakaladı", lastRead == "Dokuz Sistem", $"got={lastRead}");
            }

            Console.WriteLine("=== TEST N5: Madde 4 - Tip bilmeden erişim DOĞRULUK ===");
            {
                var dc = DynamicClass.CreateClass("UntypedTest")
                    .AddProperty<int>("Id")
                    .AddProperty<string>("Name")
                    .AddProperty<decimal>("Amount");

                dc.SetValue("Id", (object)42);
                dc.SetValue("Name", (object)"Dokuz Sistem");
                dc.SetValue("Amount", (object)99.5m);

                object? idObj = dc.GetValue("Id");
                object? nameObj = dc.GetValue("Name");
                object? amountObj = dc.GetValue("Amount");

                Check("Id doğru tipte ve değerde (boxed int)", idObj is int idVal && idVal == 42, $"got={idObj} ({idObj?.GetType()})");
                Check("Name doğru", nameObj is string s && s == "Dokuz Sistem", $"got={nameObj}");
                Check("Amount doğru tipte ve değerde (boxed decimal)", amountObj is decimal amt && amt == 99.5m, $"got={amountObj} ({amountObj?.GetType()})");

                Check("Typed GetValue<int> ile de aynı sonuç", dc.GetValue<int>("Id") == 42);

                var dc2 = DynamicClass.CreateClass("OverloadResTest").AddProperty<int>("X");
                dc2.SetValue("X", 5);
                Check("SetValue(string,T) çağrısı derleniyor ve çalışıyor (overload çakışması yok)", dc2.GetValue<int>("X") == 5);
            }

            Console.WriteLine();
            Console.WriteLine("=== BENCHMARK: Typed SetValue<T>/GetValue<T> (boxing'siz) vs Untyped SetValue/GetValue(object) ===");
            RunUntypedBenchmark();

            Console.WriteLine();
            Console.WriteLine("=== EK BENCHMARK: Katman izolasyonu (DynamicEntityAccessor çıplak vs DynamicClass sarmalayıcı) ===");
            RunLayerIsolationBenchmark();

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "TÜM YENİ ÖZELLİK TESTLERİ GEÇTİ ✅" : $"{failures} TEST BAŞARISIZ ❌");
        }

        private static void RunLayerIsolationBenchmark()
        {
            const int N = 200_000;
            var props = new Dictionary<string, Type> { { "Id", typeof(int) } };
            Type t = DynamicTypeFactory.CreateType("LayerIsoTest", props);
            var ctor = DynamicEntityAccessor.GetConstructor(t);
            object inst = ctor();

            // A) Delegate'i DÖNGÜ DIŞINDA bir kez al, sadece invoke et (teorik taban çizgi)
            var setId = DynamicEntityAccessor.GetSetter<int>(t, "Id");
            var getId = DynamicEntityAccessor.GetGetter<int>(t, "Id");
            setId(inst, 1); getId(inst); // ısınma

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < N; i++) { setId(inst, i); int v = getId(inst); _ = v; }
            long allocA = GC.GetAllocatedBytesForCurrentThread() - a0;

            // B) Her çağrıda DynamicEntityAccessor.GetSetter/GetGetter'ı YENİDEN iste (DynamicClass'ın yaptığı gibi)
            DynamicEntityAccessor.GetSetter<int>(t, "Id")(inst, 1); // ısınma (cache dolsun)
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < N; i++)
            {
                DynamicEntityAccessor.GetSetter<int>(t, "Id")(inst, i);
                int v = DynamicEntityAccessor.GetGetter<int>(t, "Id")(inst);
                _ = v;
            }
            long allocB = GC.GetAllocatedBytesForCurrentThread() - b0;

            // C) DynamicClass üzerinden (gerçek kullanım şekli)
            var dc = DynamicClass.CreateClass("LayerIsoDC").AddProperty<int>("Id");
            dc.SetValue("Id", 1); // ısınma
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long c0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < N; i++) { dc.SetValue<int>("Id", i); int v = dc.GetValue<int>("Id"); _ = v; }
            long allocC = GC.GetAllocatedBytesForCurrentThread() - c0;

            Console.WriteLine($"  A) Delegate önceden alınmış, sadece invoke : {allocA / (double)N,6:F2} B/iterasyon (set+get)");
            Console.WriteLine($"  B) Her çağrıda GetSetter/GetGetter yeniden isteniyor : {allocB / (double)N,6:F2} B/iterasyon");
            Console.WriteLine($"  C) DynamicClass.SetValue<T>/GetValue<T> (gerçek API) : {allocC / (double)N,6:F2} B/iterasyon");
        }

        private static void RunUntypedBenchmark()
        {
            const int N = 200_000;
            var dc = DynamicClass.CreateClass("BenchUntyped").AddProperty<int>("Id");
            dc.SetValue("Id", 0);

            dc.SetValue("Id", 1);
            _ = dc.GetValue<int>("Id");
            dc.SetValue("Id", (object)1);
            _ = dc.GetValue("Id");

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long b1 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < N; i++)
            {
                dc.SetValue<int>("Id", i);
                int v = dc.GetValue<int>("Id");
                _ = v;
            }
            long typedAlloc = GC.GetAllocatedBytesForCurrentThread() - b1;

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long b2 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < N; i++)
            {
                dc.SetValue("Id", (object)i);
                object? v = dc.GetValue("Id");
                _ = v;
            }
            long untypedAlloc = GC.GetAllocatedBytesForCurrentThread() - b2;

            Console.WriteLine($"  N = {N:N0} çağrı (set+get)");
            Console.WriteLine($"  TYPED   (SetValue<int>/GetValue<int>) : {typedAlloc / 1024.0:F1} KB   |  çağrı başı: {typedAlloc / (double)N:F2} B");
            Console.WriteLine($"  UNTYPED (SetValue/GetValue(object))   : {untypedAlloc / 1024.0:F1} KB   |  çağrı başı: {untypedAlloc / (double)N:F2} B");
            Console.WriteLine("  Not: UNTYPED'daki alloc, object'e box'lanan int'lerden geliyor - bu sınırda MATEMATİKSEL OLARAK kaçınılmaz.");
        }
    }

    // v2/Extend projesinin bir gün implement edeceği türden, SADECE property içeren bir
    // interface. Bu test, configureType extension point'inin gerçekten çalıştığını kanıtlıyor -
    // v2'nin asıl konusu olan "metot gövdeli interface/interceptor" mekanizmasına GİRMİYOR.
    public interface ITestIdentifiable
    {
        int Id { get; set; }
        string Name { get; set; }
    }

    public static class ExtensionPointTests
    {
        public static void RunAll()
        {
            int failures = 0;
            void Check(string name, bool condition, string detail = "")
            {
                if (condition) Console.WriteLine($"  [OK]   {name}");
                else { Console.WriteLine($"  [FAIL] {name}  {detail}"); failures++; }
            }

            var props = new Dictionary<string, Type> { { "Id", typeof(int) }, { "Name", typeof(string) } };

            Console.WriteLine("=== TEST E1: Geriye uyumluluk - configureType verilmezse davranış AYNI ===");
            {
                Type t1 = DynamicTypeFactory.CreateType("ExtCompatTest", props);
                Type t2 = DynamicTypeFactory.CreateType("ExtCompatTest", props); // configureType yok -> şema cache çalışmalı
                Check("configureType olmadan şema cache hâlâ çalışıyor (ReferenceEquals)", ReferenceEquals(t1, t2));
            }

            Console.WriteLine("=== TEST E2: configureType ile interface implementasyonu ===");
            {
                Type t = DynamicTypeFactory.CreateType("ExtInterfaceTest", props, (tb, members) =>
                {
                    tb.AddInterfaceImplementation(typeof(ITestIdentifiable));

                    // ÖNEMLİ BULGU #1: isim eşleşmesi (get_Id/set_Id) OTOMATİK implementasyon
                    // sağlamıyor - DefineMethodOverride ile AÇIKÇA bağlamak gerekiyor.
                    // ÖNEMLİ BULGU #2: TypeBuilder.GetMethod() tip CreateType() ile tamamlanmadan
                    // ÇALIŞMIYOR - bu yüzden core artık MethodBuilder referanslarını doğrudan
                    // elden ele geçiriyor (TypeMembers.Properties/Methods parametresi).
                    tb.DefineMethodOverride(members.Properties["Id"].Get, typeof(ITestIdentifiable).GetMethod("get_Id")!);
                    tb.DefineMethodOverride(members.Properties["Id"].Set, typeof(ITestIdentifiable).GetMethod("set_Id")!);
                    tb.DefineMethodOverride(members.Properties["Name"].Get, typeof(ITestIdentifiable).GetMethod("get_Name")!);
                    tb.DefineMethodOverride(members.Properties["Name"].Set, typeof(ITestIdentifiable).GetMethod("set_Name")!);
                });

                object inst = DynamicEntityAccessor.GetConstructor(t)();

                Check("Üretilen tip ITestIdentifiable implement ediyor", typeof(ITestIdentifiable).IsAssignableFrom(t));
                Check("Instance ITestIdentifiable'a cast edilebiliyor", inst is ITestIdentifiable);

                // Interface üzerinden DOĞRUDAN eriş (hiç DynamicEntityAccessor kullanmadan!)
                var typed = (ITestIdentifiable)inst;
                typed.Id = 42;
                typed.Name = "Dokuz Sistem";

                Check("Interface üzerinden set edilen Id, interface üzerinden doğru okunuyor", typed.Id == 42, $"got={typed.Id}");
                Check("Interface üzerinden set edilen Name, interface üzerinden doğru okunuyor", typed.Name == "Dokuz Sistem", $"got={typed.Name}");

                // Çapraz doğrulama: DynamicEntityAccessor (property adıyla) da AYNI backing field'ı görüyor mu?
                var idViaAccessor = DynamicEntityAccessor.GetGetter<int>(t, "Id")(inst);
                var nameViaAccessor = DynamicEntityAccessor.GetGetter<string>(t, "Name")(inst);
                Check("DynamicEntityAccessor ile okunan Id, interface ile set edilenle AYNI", idViaAccessor == 42, $"got={idViaAccessor}");
                Check("DynamicEntityAccessor ile okunan Name, interface ile set edilenle AYNI", nameViaAccessor == "Dokuz Sistem", $"got={nameViaAccessor}");
            }

            Console.WriteLine("=== TEST E3: configureType verilince şema cache BYPASS ediliyor mu ===");
            {
                Action<TypeBuilder, DynamicTypeFactory.TypeMembers> configure = (tb, members) =>
                {
                    tb.AddInterfaceImplementation(typeof(ITestIdentifiable));
                    tb.DefineMethodOverride(members.Properties["Id"].Get, typeof(ITestIdentifiable).GetMethod("get_Id")!);
                    tb.DefineMethodOverride(members.Properties["Id"].Set, typeof(ITestIdentifiable).GetMethod("set_Id")!);
                    tb.DefineMethodOverride(members.Properties["Name"].Get, typeof(ITestIdentifiable).GetMethod("get_Name")!);
                    tb.DefineMethodOverride(members.Properties["Name"].Set, typeof(ITestIdentifiable).GetMethod("set_Name")!);
                };

                Type a = DynamicTypeFactory.CreateType("ExtBypassTest", props, configure);
                Type b = DynamicTypeFactory.CreateType("ExtBypassTest", props, configure);

                Check("configureType ile İKİ ayrı çağrı FARKLI Type üretti (cache bypass doğrulandı)", !ReferenceEquals(a, b));
                Check("Her ikisi de interface'i implement ediyor", typeof(ITestIdentifiable).IsAssignableFrom(a) && typeof(ITestIdentifiable).IsAssignableFrom(b));
            }

            Console.WriteLine("=== TEST E4: configureType YOKKEN üretilen tip interface implement ETMİYOR (kirlenme yok) ===");
            {
                Type plain = DynamicTypeFactory.CreateType("ExtPlainTest", props); // configureType yok
                Check("Sıradan CreateType çağrısı interface implement etmiyor", !typeof(ITestIdentifiable).IsAssignableFrom(plain));
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "TÜM EXTENSION POINT TESTLERİ GEÇTİ ✅" : $"{failures} TEST BAŞARISIZ ❌");
        }
    }

    public enum FeatLevel { A = 1, B = 2, C = 3 }
    public delegate void TripleHandler(int a, string b, double c);

    public class FeatTarget
    {
        public string Name { get; set; } = "ilk";

        // VB.NET "Optional" ile aynı metadata (C# varsayılanlı parametre)
        public string Opt(int a, int b = 5, string s = "x", FeatLevel lv = FeatLevel.B, decimal d = 1.5m, DateTime? dt = null)
            => $"{a}|{b}|{s}|{lv}|{d}|{(dt.HasValue ? "dt" : "null")}";

        public int Over(int a) => 1;
        public int Over(int a, int b = 0) => 2;

        public event EventHandler<int>? ValueChanged;
        public event TripleHandler? Triple;
        public static event EventHandler? StaticPing;
        private event EventHandler? HiddenEvent;

        public void RaiseValue(int v) => ValueChanged?.Invoke(this, v);
        public void RaiseTriple() => Triple?.Invoke(7, "yedi", 7.5);
        public static void RaiseStatic() => StaticPing?.Invoke(null, EventArgs.Empty);
        public void RaiseHidden() => HiddenEvent?.Invoke(this, EventArgs.Empty);
        public bool HasValueSubscribers => ValueChanged != null;

        // F7 (tipli delegate'ler)
        public int Add(int a, int b) => a + b;
        public long Wide(long x) => x * 2;
        public static string Stat(string s) => "S:" + s;
        public string Kind(object o) => "obj";
        public string Kind(string s) => "str";
        public int Hits;
        public void Hit(int n) => Hits += n;
        public string Base(FeatBase b) => b.GetType().Name;
    }

    public class FeatBase { }
    public class FeatDerived : FeatBase { }

    public class FeatFresh
    {
        // parametresiz ctor -> SetInstance yoksa her çağrıda YENİ nesne (Invoke ile aynı davranış)
        private int _n;
        public int Next() => ++_n;
        public int NextBy(int k) => _n += k;
    }

    public static class BuilderFeatureTests
    {
        private static int _fail;
        private static void Check(string label, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "OK" : "HATA")}]   {label}{(detail.Length > 0 ? " -> " + detail : "")}");
            if (!ok) _fail++;
        }

        public static void RunAll()
        {
            var t = new FeatTarget();
            var b = new EvokerBuilder(typeof(FeatTarget)).SetInstance(t);

            Console.WriteLine("=== TEST F1: Optional parametreler (VB.NET Optional) ===");
            Check("Opt(1) -> tüm varsayılanlar", b.Invoke<string>("Opt", 1) == "1|5|x|B|1,5|null", b.Invoke<string>("Opt", 1)!);
            Check("Opt(1,2) -> kalanı varsayılan", b.Invoke<string>("Opt", 1, 2) == "1|2|x|B|1,5|null");
            Check("Opt(1,2,\"y\")", b.Invoke<string>("Opt", 1, 2, "y") == "1|2|y|B|1,5|null");
            Check("GetFunc(sampleArgs 1 eleman) varsayılanlarla", b.GetFunc<string>("Opt", new object[] { 0 })(new object[] { 9 }) == "9|5|x|B|1,5|null");
            Check("Over(1) -> birebir sayı eşleşmesi önce (1)", b.Invoke<int>("Over", 1) == 1);
            Check("Over(1,2) -> 2", b.Invoke<int>("Over", 1, 2) == 2);
            try { b.Invoke("Opt"); Check("Opt() zorunlu parametresiz -> hata", false); }
            catch (MissingMethodException ex) { Check("Opt() zorunlu parametresiz -> MissingMethodException", ex.Message.Contains("overload")); }

            Console.WriteLine("=== TEST F2: Büyük/küçük harf duyarsız (VB.NET) ===");
            Check("Invoke(\"opt\") küçük harf", b.Invoke<string>("opt", 3)!.StartsWith("3|"));
            Check("GetValue(\"name\") küçük harf", b.GetValue<string>("name") == "ilk");
            b.SetValue("NAME", "yeni");
            Check("SetValue(\"NAME\") büyük harf", t.Name == "yeni");

            Console.WriteLine("=== TEST F3: Event'ler ===");
            int got = -1;
            var sub = b.AddEventHandler("ValueChanged", a => got = (int)a[1]!);
            t.RaiseValue(42);
            Check("EventHandler<int> -> 42", got == 42);
            sub.Dispose();
            Check("Dispose -> abonelik kalktı", !t.HasValueSubscribers);
            got = -1; t.RaiseValue(1);
            Check("Dispose sonrası handler çağrılmadı", got == -1);

            object?[]? triple = null;
            using (b.AddEventHandler("triple", a => triple = a)) t.RaiseTriple();
            Check("özel delegate (3 param) + küçük harf isim", triple != null && (int)triple[0]! == 7 && (string)triple[1]! == "yedi" && (double)triple[2]! == 7.5);

            bool pinged = false;
            using (new EvokerBuilder(typeof(FeatTarget)).AddEventHandler("StaticPing", _ => pinged = true)) FeatTarget.RaiseStatic();
            Check("static event (instance'sız builder)", pinged);

            try { b.AddEventHandler("HiddenEvent", _ => { }); Check("private event varsayılan builder'da görünmemeli", false); }
            catch (MissingMemberException) { Check("private event varsayılan builder'da görünmüyor", true); }
            bool hidden = false;
            using (new EvokerBuilder(typeof(FeatTarget), includeNonPublic: true).SetInstance(t).AddEventHandler("HiddenEvent", _ => hidden = true)) t.RaiseHidden();
            Check("private event includeNonPublic:true", hidden);
            Check("GetEventNames", b.GetEventNames().OrderBy(x => x).SequenceEqual(new[] { "StaticPing", "Triple", "ValueChanged" }), string.Join(",", b.GetEventNames()));

            Console.WriteLine("=== TEST F4: ForgetType / ForgetCache (unload için cache temizliği) ===");
            Check("Invoke'lardan sonra statik cache'te kayıt var", EvokerBuilder.CachedCountFor(typeof(FeatTarget)) > 0, EvokerBuilder.CachedCountFor(typeof(FeatTarget)).ToString());
            b.ForgetCache();
            Check("ForgetCache sonrası statik cache boş", EvokerBuilder.CachedCountFor(typeof(FeatTarget)) == 0);
            Check("ForgetCache sonrası tekrar çalışıyor", b.Invoke<int>("Over", 5) == 1);

            Console.WriteLine("=== TEST F5: EvokerEngine.ResolveType ===");
            Check("kısa ad (benzersiz): FeatTarget", EvokerEngine.ResolveType("FeatTarget") == typeof(FeatTarget));
            Check("tam ad", EvokerEngine.ResolveType("DSO.Core.Evoker.TestApi.FeatTarget") == typeof(FeatTarget));
            Check("tam ad büyük/küçük harf duyarsız", EvokerEngine.ResolveType("dso.core.evoker.testApi.feattarget") == typeof(FeatTarget));
            try { EvokerEngine.ResolveType("AmbigName"); Check("belirsiz kısa ad -> AmbiguousMatchException", false); }
            catch (System.Reflection.AmbiguousMatchException ex) { Check("belirsiz kısa ad -> AmbiguousMatchException (sessizce rastgele seçmiyor)", ex.Message.Contains("Dup1.AmbigName") && ex.Message.Contains("Dup2.AmbigName")); }
            Check("belirsizlik tam adla çözülüyor", EvokerEngine.ResolveType("DSO.Core.Evoker.TestApi.Dup2.AmbigName") == typeof(Dup2.AmbigName));
            try { EvokerEngine.ResolveType("YokBoyleBirTip_123"); Check("olmayan tip -> TypeLoadException", false); }
            catch (TypeLoadException) { Check("olmayan tip -> TypeLoadException", true); }
            Check("EvokerEngine.Invoke kısa adla çalışıyor", (int)EvokerEngine.Invoke("FeatTarget", "Over", false, 5)! == 1);

            Console.WriteLine("=== TEST F6: ResolveType bulunamayan ad cache'i ===");
            var miss = "NegCacheTip_" + Guid.NewGuid().ToString("N");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { EvokerEngine.ResolveType(miss); } catch (TypeLoadException) { }
            var first = sw.Elapsed; sw.Restart();
            bool threw = false;
            try { EvokerEngine.ResolveType(miss); } catch (TypeLoadException) { threw = true; }
            var second = sw.Elapsed;
            Check("ikinci ıska yine TypeLoadException", threw);
            Check("ikinci ıska taramıyor (çok daha hızlı)", second.Ticks * 5 < first.Ticks, $"ilk {first.TotalMilliseconds:0.00} ms, ikinci {second.TotalMilliseconds:0.000} ms");
            // Yeni assembly yüklenince (burada dinamik assembly) aynı ad artık BULUNMALI - negatif cache geçersiz sayılır.
            var ab = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new System.Reflection.AssemblyName("NegCacheAsm_" + Guid.NewGuid().ToString("N")), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var tb = ab.DefineDynamicModule("m").DefineType(miss, System.Reflection.TypeAttributes.Public);
            var created = tb.CreateType()!;
            Type? found = null;
            try { found = EvokerEngine.ResolveType(miss); } catch (TypeLoadException) { }
            Check("yeni assembly yüklenince aynı ad bulunuyor", found == created);

            Console.WriteLine("=== TEST F7: Tipli delegate'ler (GetFunc / GetAction - boxing yok) ===");
            var add = b.GetFunc<int, int, int>("Add");
            Check("GetFunc<int,int,int>(Add)", add(2, 3) == 5);
            Check("argüman dönüşümü int -> long (Wide)", b.GetFunc<int, long>("Wide")(21) == 42);
            Check("dönüş dönüşümü long -> object", (long)b.GetFunc<long, object>("Wide")(5L) == 10L);
            Check("static metot", new EvokerBuilder(typeof(FeatTarget)).GetFunc<string, string>("Stat")("x") == "S:x");
            Check("overload tipe göre: string -> Kind(string)", b.GetFunc<string, string>("Kind")("a") == "str");
            Check("overload tipe göre: object -> Kind(object)", b.GetFunc<object, string>("Kind")("a") == "obj");
            Check("türetilmiş tip -> taban parametre", b.GetFunc<FeatDerived, string>("Base")(new FeatDerived()) == "FeatDerived");
            Check("optional parametreler varsayılanla (Opt(int))", b.GetFunc<int, string>("Opt")(4) == "4|5|x|B|1,5|null");
            Check("büyük/küçük harf duyarsız isim", b.GetFunc<int, int, int>("add")(1, 1) == 2);
            t.Hits = 0;
            var hit = b.GetAction<int>("Hit");
            hit(3); hit(4);
            Check("GetAction<int>", t.Hits == 7);
            var fresh = new EvokerBuilder(typeof(FeatFresh)).GetFunc<int, int>("NextBy");
            Check("SetInstance yok -> her çağrıda yeni nesne (Invoke ile aynı)", fresh(1) == 1 && fresh(1) == 1);
            // Aşırı yükleme ayrımı: tek tip parametreli GetFunc<int> ESKİ (object[] tabanlı) sürüm olarak kalmalı.
            Func<object[], int> old = b.GetFunc<int>("Add", new object[] { 0, 0 });
            Check("GetFunc<int>(ad, sampleArgs) eski object[] sürümü (aşırı yükleme karışmıyor)", old(new object[] { 2, 2 }) == 4);
            Action<object[]> oldAct = b.GetAction("Hit");
            Check("GetAction(ad) eski object[] sürümü", oldAct != null);
            try { b.GetFunc<string, int, int>("Add"); Check("uyumsuz argüman tipi -> hata", false); }
            catch (MissingMethodException) { Check("uyumsuz argüman tipi -> hata", true); }
            catch (InvalidCastException ex) { Check("uyumsuz argüman tipi -> InvalidCastException", ex.Message.Contains("String")); }
            try { b.GetFunc<int, int>("Hit"); Check("void metot GetFunc -> hata", false); }
            catch (InvalidOperationException ex) { Check("void metot GetFunc -> açık hata", ex.Message.Contains("GetAction")); }

            Console.WriteLine("=== TEST F8: Invoke hızlı yolu - aynı builder'da farklı imzalar karışmıyor ===");
            Check("Over(1) sonra Over(1,2) sonra Over(1)", b.Invoke<int>("Over", 1) == 1 && b.Invoke<int>("Over", 1, 2) == 2 && b.Invoke<int>("Over", 1) == 1);
            Check("Kind(\"a\") / Kind(5) argüman tipine göre", b.Invoke<string>("Kind", "a") == "str" && b.Invoke<string>("Kind", 5) == "obj" && b.Invoke<string>("Kind", "b") == "str");
            Check("Invoke<object> ile Invoke<int> aynı metot, farklı dönüş tipi", (int)b.Invoke<object>("Add", 1, 2)! == 3 && b.Invoke<int>("Add", 1, 2) == 3);
            var other = new FeatTarget();
            var b2 = new EvokerBuilder(typeof(FeatTarget)).SetInstance(other);
            b2.Execute("Hit", 10); b.Execute("Hit", 1);
            Check("iki builder farklı instance'lara gidiyor", other.Hits == 10 && t.Hits == 8, $"{other.Hits} / {t.Hits}");
            long before = GC.GetAllocatedBytesForCurrentThread();
            var a5 = new object[] { 1, 2 };
            for (int i = 0; i < 1000; i++) b.Invoke<int>("Add", a5);
            long per = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
            Check("Invoke<int> çağrı başına allocation ~0 (sadece dönüş boxing'i olabilir)", per <= 32, per + " B");
            b.ForgetCache();
            Check("ForgetCache tipli/argüman tipli girdileri de siliyor", EvokerBuilder.CachedCountFor(typeof(FeatTarget)) == 0);

            Console.WriteLine(_fail == 0 ? "\nTÜM BUILDER ÖZELLİK TESTLERİ GEÇTİ ✅" : $"\n{_fail} BUILDER ÖZELLİK TESTİ BAŞARISIZ ❌");
            if (_fail > 0) Environment.ExitCode = 1;
        }
    }

    namespace Dup1 { public class AmbigName { } }
    namespace Dup2 { public class AmbigName { } }

    // ---------------- Test hedefleri ----------------

    public enum CmdLevel { Low = 1, Mid = 2, High = 3 }

    public class CmdAddress { public string City { get; set; } = ""; public string? Street { get; set; } }

    public class CmdCustomer
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public CmdAddress? Address { get; set; }
        public List<string> Tags { get; set; } = new();
    }

    public class CmdSaveResult { public int Id { get; set; } public string Code { get; set; } = ""; public string City { get; set; } = ""; public int TagCount { get; set; } }

    public class CmdService
    {
        private int _secret = 7;
        public int Counter;
        public readonly int Fixed = 10;
        public const string Version = "1.0";
        public int BatchSize { get; set; } = 100;
        public string ReadOnlyProp => "ro";
        public CmdLevel Level { get; set; } = CmdLevel.Low;
        public static string StaticNote = "not";

        public int Add(int a, int b) => a + b;
        public double Add(double a, double b) => a + b + 0.5;
        public string Greet(string name, string greeting = "Merhaba") => $"{greeting}, {name}!";
        public string Kind(object o) => "obj";
        public string Kind(string s) => "str";
        public string Combine(int a, int b) => (a * 10 + b).ToString();
        public string Combine(string a, string b) => a + "+" + b;
        public string Pick(long x) => "long";
        public string Pick(decimal x) => "decimal";
        public CmdSaveResult SaveCustomer(CmdCustomer c) => new() { Id = 42, Code = c.Code, City = c.Address?.City ?? "", TagCount = c.Tags.Count };
        public async Task<int> AddAsync(int a, int b) { await Task.Delay(5); return a + b; }
        public async Task TouchAsync(int v) { await Task.Delay(5); Counter = v; }
        public ValueTask<int> TwiceAsync(int x) => new(x * 2);
        public int Increment() => ++Counter;
        public CmdLevel Next(CmdLevel l) => l == CmdLevel.High ? CmdLevel.High : l + 1;
        public int Divide(int a, int b) => a / b;
        public static int StaticTwice(int x) => x * 2;
        private int Hidden(int x) => x + _secret;
        public void Fail() => throw new InvalidOperationException("kasıtlı hata");
        public DateTime When(DateTime d) => d.AddDays(1);
        public Guid Echo(Guid g) => g;
        public List<int> Range(int n) => Enumerable.Range(1, n).ToList();
    }

    public static class CmdKur
    {
        public static decimal Rate { get; set; } = 30m;
        public static decimal Convert(decimal amount) => amount * Rate;
    }

    public class CmdWithCtor
    {
        public string Conn { get; }
        public long Limit { get; }
        public CmdLevel Level { get; }
        public CmdWithCtor(string conn, long limit = 50, CmdLevel level = CmdLevel.Mid) { Conn = conn; Limit = limit; Level = level; }
        public string Info() => $"{Conn}|{Limit}|{Level}";
    }

    public class CmdParams
    {
        public int Sum(params int[] xs) => xs.Sum();
        public string Join(string sep, params string[] parts) => string.Join(sep, parts);
    }

    public class CmdCounter
    {
        private int _n;
        public int Inc() => ++_n;
    }

    public static class CommandTests
    {
        private static int _fail;
        private static void Check(string label, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "OK" : "HATA")}]   {label}{(detail.Length > 0 ? " -> " + detail : "")}");
            if (!ok) _fail++;
        }

        private static EvokerCommandResult Run(IEvokerTarget t, string json) => t.ExecuteAsync(EvokerCommand.Parse(json)).GetAwaiter().GetResult();
        private static string J(object? o) => JsonSerializer.Serialize(o, EvokerCommandResult.Options);
        private static string Err(EvokerCommandResult r) => r.Error == null ? "" : $"{r.Error.Code}: {r.Error.Message}";

        public static void RunAll()
        {
            var svc = new EvokerTarget(typeof(CmdService));
            var priv = new EvokerTarget(typeof(CmdService), includeNonPublic: true);

            Console.WriteLine("=== TEST C1: invoke - sıralı / isimli / overload / dönüşümler ===");
            var r = Run(svc, "{ \"op\": \"invoke\", \"member\": \"Add\", \"args\": [3, 4] }");
            Check("sıralı argüman Add(3,4)=7 (tam sayı -> int overload)", r.Success && Equals(r.Result, 7), J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"Add\", \"args\": [1.5, 2] }");
            Check("ondalık -> double overload, op verilmezse invoke", r.Success && Equals(r.Result, 4.0), J(r.Result) + Err(r));
            r = Run(svc, "{ \"op\": \"invoke\", \"member\": \"Greet\", \"args\": { \"name\": \"Ali\" } }");
            Check("isimli argüman + optional varsayılanı", r.Success && (string?)r.Result == "Merhaba, Ali!", J(r.Result) + Err(r));
            r = Run(svc, "{ \"op\": \"invoke\", \"member\": \"greet\", \"args\": { \"GREETING\": \"Selam\", \"Name\": \"Ayşe\" } }");
            Check("isimli argüman sırası ve büyük/küçük harf önemsiz (metot adı da)", r.Success && (string?)r.Result == "Selam, Ayşe!", J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"Kind\", \"args\": [\"a\"] }");
            Check("overload: metin -> Kind(string)", r.Success && (string?)r.Result == "str", J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"Kind\", \"args\": [5] }");
            Check("overload: sayı -> Kind(object)", r.Success && (string?)r.Result == "obj", J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"Combine\", \"args\": [\"a\", \"b\"] }");
            Check("overload: Combine(string,string)", r.Success && (string?)r.Result == "a+b", J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"Pick\", \"args\": [5] }");
            Check("overload: tam sayı -> long (decimal'den önce)", r.Success && (string?)r.Result == "long", J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"Pick\", \"args\": [5], \"argTypes\": [\"decimal\"] }");
            Check("argTypes ipucu ile decimal overload", r.Success && (string?)r.Result == "decimal", J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"SaveCustomer\", \"args\": { \"c\": { \"code\": \"C001\", \"name\": \"Acme\", \"address\": { \"city\": \"İstanbul\" }, \"tags\": [\"a\",\"b\"] } } }");
            Check("nesne argüman (isimli, iç içe, liste, küçük harf alanlar)", r.Success && r.Result is CmdSaveResult { Id: 42, Code: "C001", City: "İstanbul", TagCount: 2 }, J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"SaveCustomer\", \"args\": { \"Code\": \"C002\", \"Address\": { \"City\": \"Ankara\" } } }");
            Check("tek parametreli metoda nesnenin kendisi (parametre adı yazmadan)", r.Success && r.Result is CmdSaveResult { Code: "C002", City: "Ankara" }, J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"Next\", \"args\": [\"mid\"] }");
            Check("enum adı (küçük harf) -> enum, dönüş enum", r.Success && Equals(r.Result, CmdLevel.High), J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"Next\", \"args\": [1] }");
            Check("enum sayı ile", r.Success && Equals(r.Result, CmdLevel.Mid), J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"When\", \"args\": [\"2026-01-31T10:00:00\"] }");
            Check("DateTime metinden", r.Success && r.Result is DateTime d && d.Day == 1 && d.Month == 2, J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"Echo\", \"args\": [\"11111111-2222-3333-4444-555555555555\"] }");
            Check("Guid metinden", r.Success && r.Result is Guid, J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"Range\", \"args\": [3] }");
            Check("liste dönüşü", r.Success && J(r.Result) == "[1,2,3]", J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"StaticTwice\", \"args\": [21] }");
            Check("static metot (Singleton hedefte de)", r.Success && Equals(r.Result, 42), J(r.Result) + Err(r));

            Console.WriteLine("=== TEST C2: async ===");
            r = Run(svc, "{ \"member\": \"AddAsync\", \"args\": [10, 20] }");
            Check("Task<int> beklenir", r.Success && Equals(r.Result, 30), J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"TouchAsync\", \"args\": [77] }");
            Check("Task (void) beklenir, sonuç null", r.Success && r.Result == null, Err(r));
            r = Run(svc, "{ \"op\": \"get\", \"member\": \"Counter\" }");
            Check("TouchAsync beklenmişti -> Counter=77", r.Success && Equals(r.Result, 77), J(r.Result) + Err(r));
            r = Run(svc, "{ \"member\": \"TwiceAsync\", \"args\": [21] }");
            Check("ValueTask<int>", r.Success && Equals(r.Result, 42), J(r.Result) + Err(r));

            Console.WriteLine("=== TEST C3: get / set ===");
            r = Run(svc, "{ \"op\": \"set\", \"member\": \"BatchSize\", \"value\": 500 }");
            var g = Run(svc, "{ \"op\": \"get\", \"member\": \"batchsize\" }");
            Check("set + get property (küçük harf)", r.Success && g.Success && Equals(g.Result, 500), J(g.Result) + Err(r) + Err(g));
            r = Run(svc, "{ \"op\": \"set\", \"member\": \"Level\", \"value\": \"High\" }");
            g = Run(svc, "{ \"op\": \"get\", \"member\": \"Level\" }");
            Check("enum property metinle", r.Success && Equals(g.Result, CmdLevel.High), J(g.Result) + Err(r));
            r = Run(svc, "{ \"op\": \"set\", \"member\": \"BatchSize\", \"value\": \"250\" }");
            g = Run(svc, "{ \"op\": \"get\", \"member\": \"BatchSize\" }");
            Check("sayı metin olarak da yazılabilir (\"250\")", r.Success && Equals(g.Result, 250), J(g.Result) + Err(r));
            r = Run(svc, "{ \"op\": \"get\", \"member\": \"Fixed\" }");
            Check("readonly field okunur", r.Success && Equals(r.Result, 10), Err(r));
            r = Run(svc, "{ \"op\": \"set\", \"member\": \"Fixed\", \"value\": 1 }");
            Check("readonly field yazılamaz -> InvalidOperation", !r.Success && r.Error!.Code == EvokerErrorCodes.InvalidOperation, Err(r));
            r = Run(svc, "{ \"op\": \"get\", \"member\": \"Version\" }");
            Check("const okunur", r.Success && (string?)r.Result == "1.0", Err(r));
            r = Run(svc, "{ \"op\": \"set\", \"member\": \"ReadOnlyProp\", \"value\": \"x\" }");
            Check("set'i olmayan property -> InvalidOperation", !r.Success && r.Error!.Code == EvokerErrorCodes.InvalidOperation, Err(r));
            r = Run(svc, "{ \"op\": \"get\", \"member\": \"StaticNote\" }");
            Check("static field okunur", r.Success && (string?)r.Result == "not", Err(r));

            Console.WriteLine("=== TEST C4: private üyeler ===");
            r = Run(svc, "{ \"member\": \"Hidden\", \"args\": [1] }");
            Check("varsayılan hedefte private metot görünmez -> MemberNotFound", !r.Success && r.Error!.Code == EvokerErrorCodes.MemberNotFound, Err(r));
            r = Run(priv, "{ \"member\": \"Hidden\", \"args\": [1] }");
            Check("includeNonPublic: private metot", r.Success && Equals(r.Result, 8), Err(r));
            r = Run(priv, "{ \"op\": \"get\", \"member\": \"_secret\" }");
            Check("includeNonPublic: private field", r.Success && Equals(r.Result, 7), Err(r));

            Console.WriteLine("=== TEST C5: hatalar ===");
            r = Run(svc, "{ \"member\": \"YokBoyle\" }");
            Check("olmayan metot -> MemberNotFound + mevcut üyeler listesi", !r.Success && r.Error!.Code == EvokerErrorCodes.MemberNotFound && r.Error.Message.Contains("Greet"), Err(r));
            r = Run(svc, "{ \"member\": \"Add\", \"args\": [\"x\", \"y\"] }");
            Check("uymayan argüman -> InvalidArguments + imzalar", !r.Success && r.Error!.Code == EvokerErrorCodes.InvalidArguments && r.Error.Message.Contains("Add("), Err(r));
            r = Run(svc, "{ \"member\": \"Fail\" }");
            Check("hedef kod hatası -> TargetException + tip adı", !r.Success && r.Error!.Code == EvokerErrorCodes.TargetException && r.Error.ExceptionType == "System.InvalidOperationException" && r.Error.Message == "kasıtlı hata", Err(r));
            r = Run(svc, "{ \"op\": \"uç\", \"member\": \"Add\" }");
            Check("bilinmeyen op -> BadRequest", !r.Success && r.Error!.Code == EvokerErrorCodes.BadRequest, Err(r));
            r = Run(svc, "{ \"op\": \"set\", \"member\": \"BatchSize\" }");
            Check("set'te value yok -> BadRequest", !r.Success && r.Error!.Code == EvokerErrorCodes.BadRequest, Err(r));
            var bad = new EvokerCatalog().ExecuteAsync(Guid.NewGuid(), "{ bozuk json").GetAwaiter().GetResult();
            Check("bozuk JSON -> BadRequest (exception değil)", !bad.Success && bad.Error!.Code == EvokerErrorCodes.BadRequest, Err(bad));
            r = svc.ExecuteAsync(new EvokerCommand { Member = "AddAsync", Args = JsonDocument.Parse("[1,2]").RootElement, TimeoutMs = 1 }).GetAwaiter().GetResult();
            Check("timeout -> Timeout", !r.Success && r.Error!.Code == EvokerErrorCodes.Timeout, Err(r));
            Check("HTTP eşlemesi", EvokerErrorCodes.HttpStatus("MemberNotFound") == 404 && EvokerErrorCodes.HttpStatus("InvalidArguments") == 422 && EvokerErrorCodes.HttpStatus("TargetException") == 500);

            Console.WriteLine("=== TEST C6: çok adımlı + batch ===");
            r = Run(new EvokerTarget(typeof(CmdService)), "{ \"steps\": [ { \"op\": \"set\", \"member\": \"BatchSize\", \"value\": 7 }, { \"op\": \"invoke\", \"member\": \"Add\", \"args\": [1, 2], \"as\": \"toplam\" }, { \"op\": \"get\", \"member\": \"BatchSize\" } ] }");
            Check("3 adım, sırayla, sonuçlar ve etiket", r.Success && r.Steps!.Count == 3 && Equals(r.Steps[1].Result, 3) && r.Steps[1].As == "toplam" && Equals(r.Steps[2].Result, 7), J(r.Steps) + Err(r));
            r = Run(svc, "{ \"steps\": [ { \"member\": \"Divide\", \"args\": [1, 0] }, { \"member\": \"Add\", \"args\": [1, 1] } ] }");
            Check("hata: StopOnError (varsayılan) -> sonraki adım Skipped, StepIndex=0", !r.Success && r.Error!.StepIndex == 0 && r.Steps![1].Skipped == true, J(r.Steps));
            r = Run(svc, "{ \"steps\": [ { \"member\": \"Divide\", \"args\": [1, 0] }, { \"member\": \"Add\", \"args\": [1, 1] } ], \"stopOnError\": false }");
            Check("stopOnError=false -> sonraki adım çalışır", !r.Success && r.Steps![1].Success && Equals(r.Steps[1].Result, 2), J(r.Steps));
            r = Run(svc, "{ \"op\": \"batch\", \"member\": \"Add\", \"argsList\": [[1, 1], [2, 2], { \"a\": 3, \"b\": 3 }] }");
            Check("batch (sıralı ve isimli karışık)", r.Success && J(r.Result) == "[2,4,6]", J(r.Result) + Err(r));
            r = Run(svc, "{ \"op\": \"batch\", \"member\": \"Divide\", \"argsList\": [[4, 2], [1, 0], [9, 3]] }");
            Check("batch hata -> BatchIndex=1", !r.Success && r.Error!.BatchIndex == 1 && r.Error.ExceptionType == "System.DivideByZeroException", Err(r));

            Console.WriteLine("=== TEST C7: nesne ömrü (DI gibi) ===");
            var single = new EvokerTarget(typeof(CmdCounter), EvokerLifetime.Singleton);
            Run(single, "{ \"member\": \"Inc\" }");
            r = Run(single, "{ \"member\": \"Inc\" }");
            Check("Singleton: komutlar arası durum korunur (2)", Equals(r.Result, 2), J(r.Result));
            var scoped = new EvokerTarget(typeof(CmdCounter), EvokerLifetime.Scoped);
            r = Run(scoped, "{ \"steps\": [ { \"member\": \"Inc\" }, { \"member\": \"Inc\" } ] }");
            var r2 = Run(scoped, "{ \"member\": \"Inc\" }");
            Check("Scoped: komut içinde aynı nesne (1,2), sonraki komut yeni (1)", Equals(r.Steps![1].Result, 2) && Equals(r2.Result, 1), J(r.Steps) + J(r2.Result));
            var transient = new EvokerTarget(typeof(CmdCounter), EvokerLifetime.Transient);
            r = Run(transient, "{ \"steps\": [ { \"member\": \"Inc\" }, { \"member\": \"Inc\" } ] }");
            Check("Transient: her adım yeni nesne (1,1)", Equals(r.Steps![0].Result, 1) && Equals(r.Steps[1].Result, 1), J(r.Steps));
            var stat = new EvokerTarget(typeof(CmdKur));
            Check("static sınıf otomatik Static", stat.Lifetime == EvokerLifetime.Static);
            r = Run(stat, "{ \"member\": \"Convert\", \"args\": [2] }");
            Check("Static hedef: static metot", r.Success && Equals(r.Result, 60m), J(r.Result) + Err(r));
            r = Run(new EvokerTarget(typeof(CmdCounter), EvokerLifetime.Static), "{ \"member\": \"Inc\" }");
            Check("Static hedefte instance üyesi -> InvalidOperation", !r.Success && r.Error!.Code == EvokerErrorCodes.InvalidOperation, Err(r));
            var inst = new CmdCounter();
            var fixedT = EvokerTarget.ForInstance(inst);
            Run(fixedT, "{ \"member\": \"Inc\" }");
            Check("hazır nesne hedefi: aynı nesne", inst.Inc() == 2);

            Console.WriteLine("=== TEST C8: constructor seçimi (parametresiz şartı yok) ===");
            var ct = new EvokerTarget(typeof(CmdWithCtor)).WithConstructorJson(JsonDocument.Parse("{ \"conn\": \"Server=x\", \"level\": \"High\" }").RootElement);
            r = Run(ct, "{ \"member\": \"Info\" }");
            Check("JSON isimli constructor argümanı + optional varsayılan + enum adı", r.Success && (string?)r.Result == "Server=x|50|High", J(r.Result) + Err(r));
            var ct2 = new EvokerTarget(typeof(CmdWithCtor), constructorArgs: new object?[] { "S", 7 });
            r = Run(ct2, "{ \"member\": \"Info\" }");
            Check("CLR argümanlar: int -> long dönüşümü, eksik optional varsayılan", r.Success && (string?)r.Result == "S|7|Mid", J(r.Result) + Err(r));
            var b = new EvokerBuilder(typeof(CmdWithCtor)).SetConstructor("Q");
            Check("EvokerBuilder.SetConstructor: tek argüman, kalanlar optional", b.Invoke<string>("Info") == "Q|50|Mid");
            Check("EvokerBuilder.FindConstructor", new EvokerBuilder(typeof(CmdWithCtor)).FindConstructor(new object?[] { "x" }).GetParameters().Length == 3);
            r = Run(new EvokerTarget(typeof(CmdWithCtor), EvokerLifetime.Scoped), "{ \"member\": \"Info\", \"constructorArgs\": [\"K\", 3] }");
            Check("Scoped hedefte constructorArgs komutla", r.Success && (string?)r.Result == "K|3|Mid", J(r.Result) + Err(r));
            r = Run(new EvokerTarget(typeof(CmdWithCtor)), "{ \"member\": \"Info\" }");
            Check("constructor argümanı verilmemiş -> InvalidArguments (açık mesaj)", !r.Success && r.Error!.Code == EvokerErrorCodes.InvalidArguments, Err(r));

            Console.WriteLine("=== TEST C9: katalog (Guid anahtar, izin listesi) ===");
            var cat = new EvokerCatalog();
            var k1 = cat.Register(typeof(CmdService), name: "Servis");
            var k2 = cat.Register(typeof(CmdService)); // aynı tip, isimsiz -> ayrı kayıt
            var k3 = cat.RegisterInstance(new CmdCounter(), name: "Servis");      // aynı isim - serbest (sadece açıklama)
            Check("3 kayıt, farklı Guid'ler, isim tekrarı serbest", cat.Entries.Count == 3 && k1 != k2 && cat.Find(k2)!.Name == null);
            r = cat.ExecuteAsync(k1, "{ \"member\": \"Add\", \"args\": [2, 2] }").GetAwaiter().GetResult();
            Check("Guid ile çalıştır", r.Success && Equals(r.Result, 4), Err(r));
            r = cat.ExecuteAsync(Guid.NewGuid(), "{ \"member\": \"Add\" }").GetAwaiter().GetResult();
            Check("bilinmeyen Guid -> TargetNotFound", !r.Success && r.Error!.Code == EvokerErrorCodes.TargetNotFound, Err(r));
            r = cat.ExecuteOnTypeAsync("DSO.Core.Evoker.TestApi.CmdService", EvokerCommand.Invoke("Add", 1, 2)).GetAwaiter().GetResult();
            Check("izin listesi boş -> isimle erişim NotAllowed", !r.Success && r.Error!.Code == EvokerErrorCodes.NotAllowed, Err(r));
            cat.AllowTypesFrom("DSO.Core.Evoker.TestApi");
            r = cat.ExecuteOnTypeAsync("DSO.Core.Evoker.TestApi.CmdService", EvokerCommand.Invoke("Add", 1, 2)).GetAwaiter().GetResult();
            Check("izin verilince isimle çalışır (EvokerCommand.Invoke kod içinden)", r.Success && Equals(r.Result, 3), Err(r));
            r = cat.ExecuteOnTypeAsync("System.IO.File", EvokerCommand.Invoke("Exists", "x")).GetAwaiter().GetResult();
            Check("izin dışı tip (System.IO.File) -> NotAllowed", !r.Success && r.Error!.Code == EvokerErrorCodes.NotAllowed, Err(r));
            Check("ListAllowedTypes sadece izinli tipler", cat.ListAllowedTypes("Cmd").All(t => t.Namespace == "DSO.Core.Evoker.TestApi") && cat.ListAllowedTypes("CmdService").Count == 1);
            Check("Unregister", cat.Unregister(k3) && cat.Entries.Count == 2);

            Console.WriteLine("=== TEST C10: tanım + şablon (samples) + değerler ===");
            var desc = svc.DescribeAsync(new EvokerDescribeOptions { IncludeSamples = true, IncludeValues = true }).GetAwaiter().GetResult();
            var greet = desc.Methods!.First(m => m.Name == "Greet");
            Check("imza metni", greet.Signature == "string Greet(string name, string greeting = \"Merhaba\")", greet.Signature ?? "");
            Check("metot şablonu: isimli args, optional varsayılanı",
                greet.Sample?.GetRawText() == "{\"op\":\"invoke\",\"member\":\"Greet\",\"args\":{\"name\":\"\",\"greeting\":\"Merhaba\"}}", greet.Sample?.GetRawText() ?? "");
            var save = desc.Methods!.First(m => m.Name == "SaveCustomer");
            Check("nesne parametresinin iskeleti (iç içe + liste)",
                save.Sample?.GetRawText() == "{\"op\":\"invoke\",\"member\":\"SaveCustomer\",\"args\":{\"c\":{\"Code\":\"\",\"Name\":\"\",\"Address\":{\"City\":\"\",\"Street\":\"\"},\"Tags\":[\"\"]}}}", save.Sample?.GetRawText() ?? "");
            var lvl = desc.Properties!.First(p => p.Name == "Level");
            Check("property get/set şablonu (enum ilk değer adı)", lvl.Sample != null && lvl.SampleSet?.GetRawText() == "{\"op\":\"set\",\"member\":\"Level\",\"value\":\"Low\"}", lvl.SampleSet?.GetRawText() ?? "");
            var bs = desc.Properties!.First(p => p.Name == "BatchSize");
            Check("değerler: Singleton nesnenin o anki değeri (250)", bs.Value?.GetRawText() == "250", bs.Value?.GetRawText() ?? bs.ValueError ?? "");
            var ctorD = new EvokerTarget(typeof(CmdWithCtor)).DescribeAsync(new EvokerDescribeOptions { IncludeSamples = true }).GetAwaiter().GetResult();
            Check("constructor şablonu (kayıt formu için)", ctorD.Constructors![0].Sample?.GetRawText() == "{\"conn\":\"\",\"limit\":50,\"level\":\"Mid\"}", ctorD.Constructors[0].Sample?.GetRawText() ?? "");
            var sd = stat.DescribeAsync(new EvokerDescribeOptions { IncludeValues = true }).GetAwaiter().GetResult();
            Check("static sınıf tanımı + static değer", sd.Kind == EvokerTypeKind.StaticClass && sd.Properties!.First(p => p.Name == "Rate").Value?.GetRawText() == "30", sd.Properties!.First(p => p.Name == "Rate").Value?.GetRawText() ?? "");

            Console.WriteLine("=== TEST C11: sonuç JSON'u (web) ===");
            r = Run(svc, "{ \"member\": \"SaveCustomer\", \"args\": [{ \"Code\": \"Ç1\", \"Address\": { \"City\": \"İzmir\" } }] }");
            var json = r.ToJson();
            Check("camelCase zarf, Türkçe karakter kaçışsız, null alan yok",
                json.Contains("\"success\":true") && json.Contains("\"result\":{") && json.Contains("Ç1") && json.Contains("İzmir") && !json.Contains("\"error\""), json);
            var back = EvokerCommandResult.FromJson(json);
            Check("JSON'dan geri okunur (process sınırı için)", back.Success && back.Result is JsonElement je && je.GetProperty("code").GetString() == "Ç1", J(back.Result));
            var cmdJson = EvokerCommand.Multi(EvokerCommand.Set("BatchSize", null), EvokerCommand.Invoke("Add", 1, 2)).ToJson();
            var reparsed = EvokerCommand.Parse(cmdJson);
            Check("komut JSON'a yazılıp geri okunur; set'te açık null korunur", reparsed.Steps!.Count == 2 && reparsed.Steps[0].Value.ValueKind == JsonValueKind.Null && reparsed.Steps[1].Args.GetArrayLength() == 2, cmdJson);

            Console.WriteLine("=== TEST C12: params / ParamArray ===");
            var pr = new EvokerTarget(typeof(CmdParams));
            r = Run(pr, "{ \"member\": \"Sum\", \"args\": [1, 2, 3] }");
            Check("params: argümanlar tek tek", r.Success && Equals(r.Result, 6), J(r.Result) + Err(r));
            r = Run(pr, "{ \"member\": \"Sum\", \"args\": [[4, 5]] }");
            Check("params: dizi olarak", r.Success && Equals(r.Result, 9), J(r.Result) + Err(r));
            r = Run(pr, "{ \"member\": \"Sum\" }");
            Check("params: hiç verilmezse boş dizi", r.Success && Equals(r.Result, 0), J(r.Result) + Err(r));
            r = Run(pr, "{ \"member\": \"Join\", \"args\": [\"-\", \"a\", \"b\", \"c\"] }");
            Check("params: sabit parametre + tek tek", r.Success && (string?)r.Result == "a-b-c", J(r.Result) + Err(r));
            r = Run(pr, "{ \"member\": \"Join\", \"args\": { \"sep\": \"+\", \"parts\": [\"x\", \"y\"] } }");
            Check("params: isimli (dizi)", r.Success && (string?)r.Result == "x+y", J(r.Result) + Err(r));
            r = Run(pr, "{ \"member\": \"Sum\", \"args\": [1, \"iki\"] }");
            Check("params: uymayan eleman -> InvalidArguments", !r.Success && r.Error!.Code == EvokerErrorCodes.InvalidArguments, Err(r));

            Console.WriteLine("=== TEST C13: sayısal overload tercihi (System.Math) ===");
            var math = new EvokerTarget(typeof(Math));
            r = Run(math, "{ \"member\": \"Max\", \"args\": [3, 7] }");
            Check("Math.Max(3,7) -> int overload (8 tam sayı overload'u arasından)", r.Success && r.Result is int and 7, J(r.Result) + Err(r));
            r = Run(math, "{ \"member\": \"Max\", \"args\": [1.5, 2] }");
            Check("Math.Max(1.5,2) -> double", r.Success && r.Result is double and 2.0, J(r.Result) + Err(r));
            r = Run(math, "{ \"member\": \"Max\", \"args\": [5000000000, 1] }");
            Check("int'e sığmayan değer -> long", r.Success && r.Result is long and 5000000000L, J(r.Result) + Err(r));
            r = Run(math, "{ \"member\": \"Max\", \"args\": [3, 7], \"argTypes\": [\"byte\", \"byte\"] }");
            Check("argTypes ile byte", r.Success && r.Result is byte, J(r.Result) + Err(r));
            r = Run(math, "{ \"member\": \"Abs\", \"args\": [-4] }");
            Check("Math.Abs(-4) -> int", r.Success && r.Result is int and 4, J(r.Result) + Err(r));

            Console.WriteLine(_fail == 0 ? "\nTÜM KOMUT TESTLERİ GEÇTİ ✅" : $"\n{_fail} KOMUT TESTİ BAŞARISIZ ❌");
            if (_fail > 0) Environment.ExitCode = 1;
        }
    }
}

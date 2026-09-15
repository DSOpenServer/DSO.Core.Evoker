using DSO.Core.Evoker;
using DSO.Core.Evoker.TestApi;
using System.Reflection.Emit;
using System.Text;

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
                Type t = DynamicTypeFactory.CreateType("ExtInterfaceTest", props, (tb, methodBuilders) =>
                {
                    tb.AddInterfaceImplementation(typeof(ITestIdentifiable));

                    // ÖNEMLİ BULGU #1: isim eşleşmesi (get_Id/set_Id) OTOMATİK implementasyon
                    // sağlamıyor - DefineMethodOverride ile AÇIKÇA bağlamak gerekiyor.
                    // ÖNEMLİ BULGU #2: TypeBuilder.GetMethod() tip CreateType() ile tamamlanmadan
                    // ÇALIŞMIYOR - bu yüzden core artık MethodBuilder referanslarını doğrudan
                    // elden ele geçiriyor (methodBuilders parametresi).
                    tb.DefineMethodOverride(methodBuilders["Id"].Get, typeof(ITestIdentifiable).GetMethod("get_Id")!);
                    tb.DefineMethodOverride(methodBuilders["Id"].Set, typeof(ITestIdentifiable).GetMethod("set_Id")!);
                    tb.DefineMethodOverride(methodBuilders["Name"].Get, typeof(ITestIdentifiable).GetMethod("get_Name")!);
                    tb.DefineMethodOverride(methodBuilders["Name"].Set, typeof(ITestIdentifiable).GetMethod("set_Name")!);
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
                Action<TypeBuilder, IReadOnlyDictionary<string, (MethodBuilder Get, MethodBuilder Set)>> configure = (tb, mb) =>
                {
                    tb.AddInterfaceImplementation(typeof(ITestIdentifiable));
                    tb.DefineMethodOverride(mb["Id"].Get, typeof(ITestIdentifiable).GetMethod("get_Id")!);
                    tb.DefineMethodOverride(mb["Id"].Set, typeof(ITestIdentifiable).GetMethod("set_Id")!);
                    tb.DefineMethodOverride(mb["Name"].Get, typeof(ITestIdentifiable).GetMethod("get_Name")!);
                    tb.DefineMethodOverride(mb["Name"].Set, typeof(ITestIdentifiable).GetMethod("set_Name")!);
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
}
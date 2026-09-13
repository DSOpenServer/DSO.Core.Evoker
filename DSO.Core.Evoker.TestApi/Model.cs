using DSO.Core.Evoker;
using DSO.Core.Evoker.TestApi;
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
}
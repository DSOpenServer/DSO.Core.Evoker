using System;
using System.Collections.Generic;

namespace DSO.Core.Evoker
{
    /// <summary>
    /// Bir argüman dizisinin "tip imzası": eleman sayısı + her elemanın çalışma zamanı tipi (null eleman için null).
    /// Metot/overload seçimi argüman TİPLERİNE bağlı olduğu için cache anahtarlarında kullanılır.
    ///
    /// Performans: eskiden her çağrıda string.Join + TypeHandle.ToString ile bir string üretiliyordu
    /// (çağrı başına ~300 byte ve yüzlerce ns). Bu struct 4 argümana kadar HİÇ allocation yapmaz
    /// (tipler alanlarda tutulur), daha fazlasında tek bir Type[] ayırır. Eşitlik Type referansıyla -
    /// aynı isimli iki farklı context'teki tipler (plugin reload) ayrı anahtar olur.
    /// </summary>
    public readonly struct ArgTypeKey : IEquatable<ArgTypeKey>
    {
        /// <summary>-1 = argüman dizisi null (eski "null" imzası); aksi halde eleman sayısı.</summary>
        public readonly int Count;
        private readonly Type? _t0, _t1, _t2, _t3;
        private readonly Type?[]? _rest; // Count > 4 ise TÜM tipler burada

        private ArgTypeKey(int count, Type? t0, Type? t1, Type? t2, Type? t3, Type?[]? rest)
        {
            Count = count; _t0 = t0; _t1 = t1; _t2 = t2; _t3 = t3; _rest = rest;
        }

        public static ArgTypeKey From(object?[]? args)
        {
            if (args == null) return new ArgTypeKey(-1, null, null, null, null, null);
            int n = args.Length;
            if (n > 4)
            {
                var all = new Type?[n];
                for (int i = 0; i < n; i++) all[i] = args[i]?.GetType();
                return new ArgTypeKey(n, null, null, null, null, all);
            }
            return new ArgTypeKey(n,
                n > 0 ? args[0]?.GetType() : null,
                n > 1 ? args[1]?.GetType() : null,
                n > 2 ? args[2]?.GetType() : null,
                n > 3 ? args[3]?.GetType() : null,
                null);
        }

        /// <summary>Tipler doğrudan biliniyorsa (ör. tipli GetFunc&lt;T1,T2,TResult&gt;).</summary>
        public static ArgTypeKey FromTypes(Type[] types)
        {
            int n = types.Length;
            if (n > 4) return new ArgTypeKey(n, null, null, null, null, (Type?[])types.Clone());
            return new ArgTypeKey(n,
                n > 0 ? types[0] : null, n > 1 ? types[1] : null, n > 2 ? types[2] : null, n > 3 ? types[3] : null, null);
        }

        /// <summary>i. argümanın tipi (null argüman için null).</summary>
        public Type? this[int i] => _rest != null ? _rest[i] : i switch { 0 => _t0, 1 => _t1, 2 => _t2, 3 => _t3, _ => throw new IndexOutOfRangeException() };

        /// <summary>Bu imzada verilen tip geçiyor mu (cache temizliği için).</summary>
        public bool Involves(Func<Type, bool> predicate)
        {
            for (int i = 0; i < Count; i++)
            {
                var t = this[i];
                if (t != null && predicate(t)) return true;
            }
            return false;
        }

        public bool Equals(ArgTypeKey other)
        {
            if (Count != other.Count) return false;
            if (_rest == null)
                return ReferenceEquals(_t0, other._t0) && ReferenceEquals(_t1, other._t1)
                    && ReferenceEquals(_t2, other._t2) && ReferenceEquals(_t3, other._t3);
            for (int i = 0; i < Count; i++)
                if (!ReferenceEquals(_rest[i], other._rest![i])) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is ArgTypeKey k && Equals(k);

        public override int GetHashCode()
        {
            if (_rest == null) return HashCode.Combine(Count, _t0, _t1, _t2, _t3);
            var h = new HashCode();
            h.Add(Count);
            foreach (var t in _rest) h.Add(t);
            return h.ToHashCode();
        }

        public static bool operator ==(ArgTypeKey a, ArgTypeKey b) => a.Equals(b);
        public static bool operator !=(ArgTypeKey a, ArgTypeKey b) => !a.Equals(b);

        public override string ToString()
        {
            if (Count < 0) return "null";
            var parts = new List<string>(Count);
            for (int i = 0; i < Count; i++) parts.Add(this[i]?.Name ?? "null");
            return string.Join(",", parts);
        }
    }
}
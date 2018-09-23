using System.Collections.Generic;

namespace BasicSMART
{
    static class Extensions
    {
        public static TValue Get<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dict, TKey key, TValue defaultVal)
        {
            return dict.TryGetValue(key, out TValue value) ? value: defaultVal;
        }
    }
}

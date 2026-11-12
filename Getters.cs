using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Stellar.CooldownBar;

// Bind-once typed accessors for IL2CPP proxy members (pattern: RaidManager c4f5f12 / Plugin-Performance-Rules.md §2).
//
// PropertyInfo.GetValue / MethodInfo.Invoke allocate an object[] and box every value on each call — on the 10 Hz buff
// walk and in the per-entity postfixes that was most of the plugin's ~2.6 KB per refresh. Each member is compiled ONCE
// (at resolve time, never per call) into a delegate over `object` that casts to the proxy type and reads the member
// directly: integral/enum/bool → Func<object,long>, float/double → Func<object,double>, references → Func<object,object?>,
// so value reads never box. If compiling throws, the reflection read is kept as the fallback (same value, still
// allocating) and counted, so the one-time "[Buff]/[SkillCD] getters" log line says which path is live.
// Proxy reference returns (a list, a row, a CDData) still allocate their interop wrapper — unavoidable, and small.
internal static class Getters
{
    internal static int Bound, Fallback;   // totals across all binds; read by the one-time path log lines

    // obj → declaring type → member. Member may be a PropertyInfo, FieldInfo, or a parameterless instance MethodInfo.
    private static Expression? Read(ParameterExpression p, MemberInfo m)
    {
        var inst = Expression.Convert(p, m.DeclaringType!);
        return m switch
        {
            PropertyInfo pi => Expression.Property(inst, pi),
            FieldInfo fi    => Expression.Field(inst, fi),
            MethodInfo mi   => Expression.Call(inst, mi),
            _               => null,
        };
    }

    private static Type? MemberType(MemberInfo m) => m switch
    {
        PropertyInfo pi => pi.PropertyType,
        FieldInfo fi    => fi.FieldType,
        MethodInfo mi   => mi.ReturnType,
        _               => null,
    };

    private static object? Reflect(MemberInfo m, object o) => m switch
    {
        PropertyInfo pi => pi.GetValue(o),
        FieldInfo fi    => fi.GetValue(o),
        MethodInfo mi   => mi.Invoke(o, null),
        _               => null,
    };

    private static long ToLong(object? v)
    {
        try { return v switch { null => 0L, bool b => b ? 1L : 0L, _ => Convert.ToInt64(v) }; }
        catch { return 0L; }
    }

    /// <summary>Integral / enum / bool member as an unboxed long (bool → 1/0). Null when <paramref name="m"/> is null.</summary>
    internal static Func<object, long>? Long(MemberInfo? m)
    {
        if (m == null) return null;
        try
        {
            var p = Expression.Parameter(typeof(object));
            var get = Read(p, m)!;
            var t = MemberType(m)!;
            Expression body = t == typeof(bool)
                ? Expression.Condition(get, Expression.Constant(1L), Expression.Constant(0L))
                : Expression.Convert(get, typeof(long));
            var f = Expression.Lambda<Func<object, long>>(body, p).Compile();
            Bound++;
            return f;
        }
        catch
        {
            Fallback++;
            return o => ToLong(Reflect(m, o));
        }
    }

    /// <summary>float / double (or any numeric) member as an unboxed double.</summary>
    internal static Func<object, double>? Double(MemberInfo? m)
    {
        if (m == null) return null;
        try
        {
            var p = Expression.Parameter(typeof(object));
            var f = Expression.Lambda<Func<object, double>>(Expression.Convert(Read(p, m)!, typeof(double)), p).Compile();
            Bound++;
            return f;
        }
        catch
        {
            Fallback++;
            return o => { try { return Convert.ToDouble(Reflect(m, o)); } catch { return 0d; } };
        }
    }

    /// <summary>Reference-typed member (proxy object or string).</summary>
    internal static Func<object, object?>? Obj(MemberInfo? m)
    {
        if (m == null) return null;
        try
        {
            var p = Expression.Parameter(typeof(object));
            var f = Expression.Lambda<Func<object, object?>>(Expression.Convert(Read(p, m)!, typeof(object)), p).Compile();
            Bound++;
            return f;
        }
        catch
        {
            Fallback++;
            return o => Reflect(m, o);
        }
    }

    /// <summary>An int-indexed list's <c>Count</c> + <c>get_Item(int)</c> (IL2CPP ZList / RepeatedField are not
    /// IEnumerable to us). Replaces StellarInterop.Enumerate, whose iterator + per-element object[] allocated.</summary>
    internal static bool ListAccessors(Type listType, out Func<object, long>? count, out Func<object, int, object?>? item)
    {
        count = Long(FindUp(listType, "Count"));
        item = null;
        MethodInfo? getItem = null;
        for (var cur = listType; cur != null && getItem == null; cur = cur.BaseType)
            foreach (var mi in cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (mi.Name != "get_Item") continue;
                var ps = mi.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(int)) { getItem = mi; break; }
            }
        if (getItem != null)
        {
            try
            {
                var p = Expression.Parameter(typeof(object)); var i = Expression.Parameter(typeof(int));
                item = Expression.Lambda<Func<object, int, object?>>(Expression.Convert(
                    Expression.Call(Expression.Convert(p, getItem.DeclaringType!), getItem, i), typeof(object)), p, i).Compile();
                Bound++;
            }
            catch
            {
                Fallback++;
                var gi = getItem;
                item = (o, idx) => gi.Invoke(o, new object[] { idx });
            }
        }
        return count != null && item != null;
    }

    /// <summary>Public/non-public instance property by name, walking the base chain.</summary>
    internal static PropertyInfo? FindUp(Type? t, string name)
    {
        for (var cur = t; cur != null; cur = cur.BaseType)
        {
            var p = cur.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p != null) return p;
        }
        return null;
    }
}

// --------------------------------------------------------------------------------------------
// 共享位图（SharedBitmap）在启动器程序集里是 internal：测试只用反射、编不到这个名字，
// 而 `Func<string, SharedBitmap>` 这种委托类型必须**精确匹配**才能塞进字段。
// 所以这里用泛型方法在运行时把类型参数填进去（Func<string, T> 与它才是同一个类型）。
// --------------------------------------------------------------------------------------------
using System;
using System.Drawing;
using System.Reflection;

internal static partial class HardeningTests
{
    /// <summary>造一个假的「URL → 共享位图」查询函数（模拟「图到了 / 还没到」）。</summary>
    /// <param name="inner">返回 null 表示「还没下载好」。</param>
    private static object MakeSharedLookup(Type sharedBitmapType, Func<string, Bitmap> inner)
    {
        MethodInfo maker = typeof(HardeningTests).GetMethod("BuildSharedLookup",
            BindingFlags.NonPublic | BindingFlags.Static);
        return maker.MakeGenericMethod(sharedBitmapType).Invoke(null, new object[] { inner });
    }

    private static Func<string, T> BuildSharedLookup<T>(Func<string, Bitmap> inner) where T : class
    {
        // ⚠️ 构造函数是 internal：`GetConstructor(new Type[]{...})` 只找 public，会拿到 null →
        //    下一行 Invoke 就 NRE（2026-09-27 实测踩到，别改回去）。
        ConstructorInfo ctor = typeof(T).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(Bitmap) }, null);
        MethodInfo drop = typeof(T).GetMethod("DropFromCache", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo acquire = typeof(T).GetMethod("Acquire", BindingFlags.NonPublic | BindingFlags.Instance);
        return delegate (string url)
        {
            Bitmap bitmap = inner(url);
            if (bitmap == null) return null;
            object shared = ctor.Invoke(new object[] { bitmap });
            // 顺序要紧：**先借出**再让缓存放手 —— 反过来的话 DropsFromCache 当场就把它释放了
            // （leases 还是 0）。这正是所有权模型的行为，测试得照它的规矩来。
            acquire.Invoke(shared, null);
            drop.Invoke(shared, null);
            return (T)shared;
        };
    }

    /// <summary>取句柄底下的 Bitmap（SharedBitmap.Bitmap）。</summary>
    private static Bitmap BitmapOf(object shared)
    {
        if (shared == null) return null;
        return (Bitmap)shared.GetType().GetProperty("Bitmap",
            BindingFlags.NonPublic | BindingFlags.Instance).GetValue(shared, null);
    }

    /// <summary>还回借出的一份。</summary>
    private static void ReleaseShared(object shared)
    {
        if (shared == null) return;
        shared.GetType().GetMethod("Release", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(shared, null);
    }

    /// <summary>这张共享位图是否真的已经被释放（读 Width 会抛）。</summary>
    private static bool IsFreed(object shared)
    {
        try { int width = BitmapOf(shared).Width; return false; }
        catch (Exception) { return true; }
    }
}
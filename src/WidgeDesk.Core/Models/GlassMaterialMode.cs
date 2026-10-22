namespace WidgeDesk.Core.Models;

public enum GlassMaterialMode
{
    AndroidLiquidGlass,
    PhysicalGlass,

    /// <summary>
    /// 已下线的磨砂材质。
    /// </summary>
    /// <remarks>
    /// 只作为旧 <c>settings.json</c> 的反序列化别名保留：删掉这个枚举值会让写着
    /// <c>"FrostedGlass"</c> 的旧文件解析失败，进而被判断成「配置损坏」并丢给用户一份默认设置。
    /// 加载时 <c>WidgeDeskSettings.Normalize</c> 会把它归一成液态玻璃，产品不再渲染这个材质。
    /// </remarks>
    FrostedGlass,
}

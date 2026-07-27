# 尺寸计算与坐标变换接口

状态：SBMS 1.0.0  
Rust 模块：`sbms::geometry`  
配置模块：`sbms::config`

本文记录 GUI 可以依赖的现有核心接口。GUI 只负责收集参数、展示预览和
请求保存，不得复制尺寸公式、根据宽高猜测旋转或自行读写配置文件。

## 当前能力边界

`SizingRequest::calculate()` 是纯计算接口：它根据参考显示器和目标显示器
的像素、物理尺寸、旋转及缩放策略，返回计划使用的虚拟显示模式。

SBMS 1.0.0 的 IDD 仍固定报告 `3840x2160@240`。计算结果尚未下发给驱动，
`preferred_refresh_millihz` 也只是随结果返回的偏好值。GUI 在驱动支持动态
模式前只能把结果标为“计划模式”或“预览”，不能声称已经应用。

## 公开数据类型

### 显示器输入

| 类型 | 字段或取值 | 含义 |
| --- | --- | --- |
| `PixelSize` | `width: u32`, `height: u32` | 原生像素尺寸；宽高必须非零 |
| `PhysicalMeasurement` | `DimensionsMm { width, height }` | 明确的物理宽高，单位毫米 |
| `PhysicalMeasurement` | `DiagonalMm(f64)` | 物理对角线，核心单位毫米；GUI 可以用英寸输入并乘以 `25.4` |
| `AspectRatio` | `width: u32`, `height: u32` | 可选的显式物理宽高比；两项都必须非零 |
| `Rotation` | `Deg0`, `Deg90`, `Deg180`, `Deg270` | 明确的顺时针旋转；默认 `Deg0` |
| `DisplayGeometry` | `native_pixels`, `physical`, `aspect_ratio`, `rotation` | 一台显示器的完整几何输入 |

`native_pixels` 和明确的 `DimensionsMm` 都描述显示器未旋转时的数据。
计算时先应用 `rotation`；90° 和 270° 会同时交换像素宽高与物理宽高。
不要通过 `width > height` 判断旋转方向。

当 `physical` 是 `DiagonalMm` 时，`aspect_ratio: Some(...)` 会决定从对角线
推导出的物理宽高；`None` 为旧配置兼容路径，继续使用 `native_pixels` 的比例。
GUI 不得只展示比例而不把它传入计算。

### 计算请求

```rust
pub struct SizingRequest {
    pub reference: DisplayGeometry,
    pub target: DisplayGeometry,
    pub strategy: SizingStrategy,
    pub alignment: u32,
    pub preferred_refresh_millihz: Option<u32>,
}
```

- `reference`：提供期望像素密度的参考显示器。
- `target`：实际承载映射画面的物理显示器。
- `strategy`：尺寸策略，默认 `MatchPhysicalSize`。
- `alignment`：输出宽高的对齐要求，Serde 缺省值为 2。
- `preferred_refresh_millihz`：刷新率偏好，单位千分之一 Hz；例如
  240 Hz 写作 `Some(240_000)`。

`SizingStrategy` 有三种取值：

- `MatchPhysicalSize`：使用参考显示器的横向像素密度和目标显示器的物理
  宽度计算理想宽度，然后保持目标显示器宽高比并满足对齐要求。
- `RoundedScale`：把理想缩放倍率四舍五入到最近的 `0.25×`，然后保持
  目标显示器宽高比并满足对齐要求。该策略在物理尺寸精度与规整分辨率之间
  取得平衡。
- `IntegerScale`：把理想缩放倍率四舍五入到最近的正整数倍，最小为 `1×`，
  然后保持目标显示器宽高比并满足对齐要求。

旧版配置中的 `{"integer_scale":{"max_scale":N}}` 仍可读取；合法的
`1..=8` 会迁移为新的无参数 `IntegerScale`，下一次保存时写成
`"integer_scale"`。

### 计算结果

```rust
pub struct SizingResult {
    pub virtual_mode: PixelSize,
    pub oriented_target: PixelSize,
    pub scale_x: f64,
    pub scale_y: f64,
    pub preferred_refresh_millihz: Option<u32>,
}
```

- `virtual_mode`：建议的虚拟显示模式。
- `oriented_target`：应用旋转后的目标像素尺寸。
- `scale_x`、`scale_y`：`virtual_mode / oriented_target`。
- `preferred_refresh_millihz`：原样传递的刷新率偏好。

`SizingResult` 是派生值，不应写入配置。持久化原始 `SizingRequest`，每次
读取后重新计算，才能让校验和后续算法升级保持一致。

## 校验约束

`calculate()` 返回 `Result<SizingResult, GeometryError>`，GUI 应直接展示
错误文本，不要静默修正用户输入。

| 输入 | 约束 |
| --- | --- |
| 像素宽高 | 必须非零 |
| 物理宽、高或对角线 | 必须是有限数，范围 `10..=10000` mm |
| `alignment` | `1..=256` 之间的 2 的幂 |
| `preferred_refresh_millihz` | 存在时必须大于 0 |

计算还会拒绝溢出和超出支持范围的尺寸。`RoundedScale` 在宽高比或对齐要求
无法精确表达 `0.25×` 时，会向上选择最接近且有效的像素尺寸。

## GUI 推荐调用流程

```rust
use std::error::Error;

use sbms::config::ConfigStore;
use sbms::geometry::{
    AspectRatio, DisplayGeometry, PhysicalMeasurement, PixelSize, Rotation, SizingRequest,
    SizingResult, SizingStrategy,
};

fn save_sizing(request: SizingRequest) -> Result<SizingResult, Box<dyn Error>> {
    let preview = request.calculate()?;

    let store = ConfigStore::default_store()?;
    let loaded = store.load()?;
    if let Some(warning) = loaded.warning {
        // GUI 应把这条消息交给用户，并结束本次保存流程。
        return Err(std::io::Error::other(warning).into());
    }

    let mut config = loaded.config;
    config.sizing = Some(request);
    store.save(&config)?;
    Ok(preview)
}

fn example() -> Result<(), Box<dyn Error>> {
    let preview = save_sizing(SizingRequest {
        reference: DisplayGeometry {
            native_pixels: PixelSize {
                width: 3840,
                height: 2160,
            },
            physical: PhysicalMeasurement::DiagonalMm(685.8),
            aspect_ratio: Some(AspectRatio { width: 16, height: 9 }),
            rotation: Rotation::Deg0,
        },
        target: DisplayGeometry {
            native_pixels: PixelSize {
                width: 2560,
                height: 1440,
            },
            physical: PhysicalMeasurement::DiagonalMm(609.6),
            aspect_ratio: None,
            rotation: Rotation::Deg0,
        },
        strategy: SizingStrategy::MatchPhysicalSize,
        alignment: 2,
        preferred_refresh_millihz: Some(240_000),
    })?;
    println!("planned mode: {:?}", preview.virtual_mode);
    Ok(())
}
```

GUI 实现时按以下顺序处理：

1. 从系统显示器信息和用户输入构造 `SizingRequest`。
2. 每次字段改变都可以调用 `calculate()` 刷新预览；该函数无 I/O 和全局
   状态。
3. 只有计算成功后才允许保存。
4. 使用 `ConfigStore::load()` 和 `save()`，不要直接编辑
   `%LOCALAPPDATA%\SBMS\config-v1.json`。
5. 如果 `LoadOutcome.warning` 非空，保留原文件且禁用自动保存；只有明确的
   用户操作才可以调用 `ConfigStore::reset()`。
6. 保存 `SizingRequest`，不要保存或反向推导 `SizingResult`。

## 配置中的 JSON 形状

以下内容展示 `AppConfig.sizing` 的稳定 Serde 形状：

```json
{
  "version": 1,
  "target_id": null,
  "sizing": {
    "reference": {
      "native_pixels": {
        "width": 3840,
        "height": 2160
      },
      "physical": {
        "diagonal_mm": 685.8
      },
      "rotation": "deg0"
    },
    "target": {
      "native_pixels": {
        "width": 2560,
        "height": 1440
      },
      "physical": {
        "dimensions_mm": {
          "width": 527.0,
          "height": 296.0
        }
      },
      "rotation": "deg0"
    },
    "strategy": "rounded_scale",
    "alignment": 2,
    "preferred_refresh_millihz": 240000
  }
}
```

JSON 是配置的存储格式，不是建议 GUI 绕过 `ConfigStore` 的文件接口。

## 坐标变换接口

GUI 本身通常不需要映射鼠标坐标，但预览、裁剪或后续交互层必须复用同一
接口：

```rust
let transform = CoordinateTransform::stretch(target, source, rotation)?;
let mapped = transform.map_target_point(point);
```

- `target` 和 `source` 使用 `PixelRect`，宽高必须非零且不能超过 Win32
  `i32` 坐标范围。
- `map_target_point()` 对目标矩形外的点返回 `None`。
- 目标矩形内的点按显式旋转映射到源矩形。
- 这是当前鼠标输入转发实际使用的路径；前端不得维护另一套比例公式。

## 后续接入驱动时的责任边界

动态模式落地后，建议保持以下单向数据流：

```text
GUI 输入
  -> SizingRequest
  -> calculate()
  -> SizingResult 预览
  -> 保存 SizingRequest
  -> 映射控制器请求驱动应用 virtual_mode / refresh
  -> 驱动确认后的实际模式
```

驱动拒绝模式时应保留用户请求并报告实际模式，不能把“计算成功”等同于
“驱动应用成功”。

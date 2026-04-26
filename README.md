# ImageAvatar

**English** | [中文](#中文说明)

A WPF desktop application for automated garment pattern extraction and mockup generation, powered by the U-2-Net AI model.

---

## Features

- **AI Background Removal** — U-2-Net ONNX inference extracts patterns from garment photos with a transparent background.
- **Batch Mockup Synthesis** — SkiaSharp Multiply-blend compositing overlays extracted patterns onto template garments.
- **Quality Control** — Side-by-side and overlay comparison canvas with linked zoom/pan; approve or reject each result.
- **Pipeline Dashboard** — Real-time file counts across the six-folder POD workflow.
- **Multilingual UI** — Runtime language switching: 简体中文, English, Français, 繁體中文.

---

## 00–51 Folder Workflow

```
Drop garment photos
       ↓
00_提图队列   (Extract Queue)   ← Drop source images here
       ↓  [AI Extraction – U-2-Net ONNX, removes background]
01_提图完成   (Extract Done)    → Transparent-background PNG

       ↓  (copy/move files to continue)
30_抠图队列   (Refine Queue)    ← Manual refinement input
       ↓  [Optional manual edge-refine step]
31_抠图完成   (Refine Done)

       ↓
50_成品队列   (Finalize Queue)  ← Patterns ready for mockup
       ↓  [Batch Mockup – SkiaSharp Multiply blend]
51_成品完成   (Finalize Done)   → Final composited mockups

       ↓  [QC Review]
Production_Ready /  Rework
```

All six folders are created automatically when you set the **Root Path** and click **Apply** or enable **Watch** mode. The repo ships with a `workspace/` directory containing `.gitkeep` placeholders so the structure is preserved on clone.

---

## AI Model Requirements

ImageAvatar uses **U-2-Net** for background removal. The model is **not bundled** with the repo (≈ 176 MB).

### Download

```bash
# Download u2net.onnx from the rembg project
https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx
```

Place the file at:

```
%AppData%\ImageAvatar\models\u2net.onnx
```

Or configure a custom path in **Settings → AI Model → Browse…**.

### Specifications

| Property  | Value                      |
|-----------|----------------------------|
| Input     | `[1, 3, 320, 320]` float32 |
| Normalize | ImageNet mean/std (RGB)    |
| Output    | `[1, 1, 320, 320]` float32 |
| Runtime   | Microsoft.ML.OnnxRuntime   |

---

## Configuration

The app reads startup defaults from `appsettings.json` in the executable directory. This file is ideal for **POD environment** deployment where paths are fixed across workstations.

```json
{
  "ImageAvatar": {
    "WorkspaceRoot":   "\\\\NAS01\\POD\\workspace",
    "ModelPath":       "\\\\NAS01\\POD\\models\\u2net.onnx",
    "TemplatesFolder": "\\\\NAS01\\POD\\templates"
  }
}
```

Leave values empty to use per-user defaults (`%Documents%\ImageAvatar`). Runtime changes saved through the Settings page are written to `%AppData%\ImageAvatar\settings.json` and take precedence over `appsettings.json`.

---

## Language Switching

1. Open **Settings → Language**.
2. Select a language from the dropdown: `zh-CN`, `en-US`, `fr-FR`, `zh-HK`.
3. Click **Apply** — the UI updates immediately without restart.

---

## Building from Source

**Prerequisites:** .NET 8 SDK, Visual Studio 2022 17.8+

```bash
git clone https://github.com/Forrest-tech/ImageAvatar.git
cd ImageAvatar
dotnet restore
dotnet build -c Release
```

Run:

```bash
dotnet run --project ImageAvatar/ImageAvatar.csproj
```

The workspace skeleton is at `workspace/` relative to the repo root. Point the Dashboard **Root Path** to that directory to use it immediately.

---

## Tech Stack

| Layer         | Library                         |
|---------------|---------------------------------|
| UI Framework  | WPF (.NET 8) + WPF-UI 3.x       |
| MVVM          | CommunityToolkit.Mvvm 8.x       |
| DI / Config   | Microsoft.Extensions.Hosting    |
| AI Inference  | Microsoft.ML.OnnxRuntime 1.20   |
| Image Processing | OpenCvSharp4               |
| Canvas / Blend | SkiaSharp 2.88               |

---

---

# 中文说明

**[English](#imageAvatar)** | 中文

基于 U-2-Net AI 模型的服装图案自动提取与效果图生成桌面应用（WPF）。

---

## 功能

- **AI 去背景** — 使用 U-2-Net ONNX 模型自动去除服装图片背景，输出透明底 PNG。
- **批量合成效果图** — SkiaSharp Multiply 混合模式将图案叠加到模板服装上，保留布料褶皱与阴影。
- **质检审核** — 支持并排与叠加对比，画布联动缩放/平移，一键通过或退回。
- **流水线仪表盘** — 实时显示六个文件夹的文件数量。
- **多语言界面** — 运行时切换语言：简体中文、English、Français、繁體中文。

---

## 00–51 文件夹工作流

```
投入服装照片
       ↓
00_提图队列   ← 将原始服装图片放入此处
       ↓  [AI 提图 – U-2-Net ONNX 去背景]
01_提图完成   → 透明底 PNG

       ↓
30_抠图队列   ← 需人工精修的图片
       ↓  [可选：手动精修边缘]
31_抠图完成

       ↓
50_成品队列   ← 准备合成效果图的图案
       ↓  [批量合成 – SkiaSharp Multiply 叠加]
51_成品完成   → 最终效果图

       ↓  [质检审核]
Production_Ready（生产就绪）/ Rework（返工）
```

设置**根目录**并点击**应用**或开启**监听**模式后，六个文件夹将自动创建。仓库中的 `workspace/` 目录包含 `.gitkeep` 占位文件，克隆后即保留目录结构。

---

## AI 模型要求

ImageAvatar 使用 **U-2-Net** 进行去背景处理。模型文件**不包含在仓库中**（约 176 MB）。

### 下载

```bash
# 从 rembg 项目下载 u2net.onnx
https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx
```

将文件放置于：

```
%AppData%\ImageAvatar\models\u2net.onnx
```

或在**设置 → AI 模型 → 浏览…** 中配置自定义路径。

---

## 配置说明

应用启动时从可执行文件目录下的 `appsettings.json` 读取默认配置，适用于 **POD 生产环境**统一部署（如网络共享路径）：

```json
{
  "ImageAvatar": {
    "WorkspaceRoot":   "\\\\NAS01\\POD\\workspace",
    "ModelPath":       "\\\\NAS01\\POD\\models\\u2net.onnx",
    "TemplatesFolder": "\\\\NAS01\\POD\\templates"
  }
}
```

留空则使用默认路径（`%Documents%\ImageAvatar`）。通过设置页面保存的配置写入 `%AppData%\ImageAvatar\settings.json`，优先级高于 `appsettings.json`。

---

## 切换语言

1. 打开**设置 → 语言**。
2. 从下拉框选择语言：`zh-CN`、`en-US`、`fr-FR`、`zh-HK`。
3. 点击**应用** — 界面立即更新，无需重启。

---

## 从源码构建

**环境要求：** .NET 8 SDK、Visual Studio 2022 17.8+

```bash
git clone https://github.com/Forrest-tech/ImageAvatar.git
cd ImageAvatar
dotnet restore
dotnet build -c Release
```

仓库根目录的 `workspace/` 即为工作流目录，在仪表盘**根目录**中填入该路径即可立即使用。

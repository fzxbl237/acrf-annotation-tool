# ACRF 注释工具

基于 Avalonia 和 .NET 10 的桌面端 ACRF 注释编辑工具，用于在 PDF 表单上查看、编辑和管理 FreeText 注释，并通过 Excel 注释 Spec 与 PDF 注释之间进行数据同步。

## 功能概览

### PDF 预览与注释

- 打开 aCRF PDF 文件并进行分页预览。
- 支持上一页、下一页和页码显示。
- 支持 PDF 缩放。
- 加载和显示 PDF FreeText 注释。
- 编辑注释的文本、坐标、宽度和行数等信息。
- 拖动和调整 PDF 注释位置。
- 删除、复制和批量调整注释。
- 支持将修改后的注释另存为新的 PDF 文件。

### 注释 Spec 导入与导出

- 从 Excel `.xlsx` 文件导入注释 Spec。
- 将当前注释数据导出为 Excel `.xlsx` 文件。
- 导入后自动同步注释到当前 PDF。
- 支持保留注释所属域、注释类型、坐标、宽度和行数等字段。

### 注释编辑操作

在注释信息表格中可以编辑和查看以下内容：

- Form 名称
- 页码
- 变量名称
- CRF 文本
- 注释文本
- 注释所属域
- 虚线框设置
- X/Y 坐标
- 注释宽度
- 行数

右键注释行可以执行：

- 删除
- 复制
- 大写转换
- 批量调整 X 坐标
- 批量调整 Y 坐标

### Domain annotation

工具会根据注释中的所属域自动生成页面顶部的 Domain annotation：

- 同一页面的不同域会生成对应的域标签。
- 域标签使用不同背景色区分。
- 标签宽度会根据文本内容自动测量。
- 可以通过工具栏右侧的设置按钮配置距离页面左上角的水平和垂直距离。
- 设置确认后会立即刷新当前 PDF 中的 Domain annotation。
- X/Y 配置会持久化保存，应用重启后自动恢复。

Domain annotation 位置配置文件位于：

```text
%AppData%\AcrfAnnotationTool\settings.json
```

默认水平和垂直距离均为 `8`。

## 运行环境

- Windows（当前主要运行环境）
- .NET SDK `10.0.301` 或兼容的 .NET 10 SDK
- Avalonia `12.1.1`

项目通过 `global.json` 固定 SDK 版本：

```json
{
  "sdk": {
    "version": "10.0.301",
    "rollForward": "latestFeature",
    "allowPrerelease": false
  }
}
```

## 快速开始

在项目根目录执行：

```powershell
dotnet restore
dotnet run
```

也可以先构建再运行：

```powershell
dotnet build
dotnet run --no-build
```

## 构建

普通构建：

```powershell
dotnet build
```

指定独立输出目录进行验证：

```powershell
dotnet build --no-restore -p:OutputPath=artifacts\verify
```

发布构建：

```powershell
dotnet publish -c Release -o publish
```

## 资源文件

项目根目录下的 `Fonts` 目录包含应用运行所需的字体资源。项目文件已配置在构建和发布时将字体复制到输出目录：

```text
Fonts\
├── ARIALUNI.TTF
├── ARIALBD.TTF
├── ARIALBI.TTF
├── ARIALI.TTF
├── ARIAL.TTF
├── ARIALN.TTF
├── ARIALNB.TTF
├── ARIALNBI.TTF
├── ARIALNI.TTF
├── ARIBLK.TTF
├── AlibabaPuHuiTi-3-55-Regular.otf
└── iconfont.ttf
```

PDF 注释文本测量优先从应用目录下的 `Fonts` 子目录加载：

```text
<AppContext.BaseDirectory>\Fonts\ARIALUNI.TTF
<AppContext.BaseDirectory>\Fonts\ARIALBD.TTF
```

当字体资源缺失时，程序会回退到 Helvetica 字体，不依赖 Windows 系统字体目录中的文件。

## 数据库资源

项目包含 `cdisc_setting.db`，用于提供 CDISC 数据集和变量域相关信息。该文件会在构建和发布时复制到输出目录。

运行时需要确保应用输出目录中存在：

```text
cdisc_setting.db
```

## 操作流程

### 打开 PDF

1. 启动应用。
2. 点击工具栏的“aCRF 导入”。
3. 选择 PDF 文件。
4. 在左侧 PDF 预览区域查看页面和注释。

### 导入注释 Spec

1. 点击“Spec 导入”。
2. 选择 Excel `.xlsx` 文件。
3. 程序读取注释数据并更新注释信息表格。
4. 如果已经打开 PDF，导入的数据会同步到 PDF 预览中。

### 编辑和保存注释

1. 在右侧注释信息表格中修改注释字段。
2. 通过 PDF 预览拖动注释可以调整位置。
3. 点击“另存 PDF”。
4. 选择输出位置后保存修改后的 PDF。

### 配置 Domain annotation 位置

1. 点击工具栏右侧的设置按钮。
2. 输入距离页面左上角的水平和垂直距离。
3. 点击确认。
4. Domain annotation 会立即重新布局。
5. 下次启动应用时会自动使用上一次保存的设置。

## 项目结构

```text
acrf-annotation-tool/
├── Controls/
│   ├── DataGrid/                 注释信息表格控件
│   └── PdfViewerControl/         PDF 预览和注释控件
├── Fonts/                        应用字体资源
├── Models/                       数据模型
├── Services/
│   ├── ApplicationSettingsStore.cs
│   ├── CdiscDatasetCatalog.cs
│   └── TemplateVariableDomainCatalog.cs
├── ViewModels/
│   ├── Documents/                文档和注释业务逻辑
│   ├── DockFactory.cs             Dock 布局工厂
│   └── MainWindowViewModel.cs
├── Views/
│   ├── AcrfEditorView.axaml       主编辑界面
│   ├── AnnotationToolView.axaml   注释信息界面
│   └── PdfToolView.axaml          PDF 预览界面
├── cdisc_setting.db              CDISC 配置数据库
├── AcrfAnnotationTool.csproj
├── App.axaml
├── MainWindow.axaml
└── global.json
```

## 主要技术栈

- .NET 10
- Avalonia 12.1.1
- AtomUI Desktop Controls
- Dock.Avalonia
- CommunityToolkit.Mvvm
- DynamicData
- iText 7
- PDFsharp
- Docnet.Core
- MiniExcel
- SqlSugarCore
- Microsoft.Extensions.DependencyInjection

## 说明

- 当前项目的注释数据以 PDF FreeText 注释和 Excel 注释 Spec 为主要交换格式。
- Domain annotation 是根据注释所属域动态生成的页面辅助标识，不作为 Excel 注释表中的独立数据行维护。
- 应用字体和 CDISC 数据库均属于运行时资源，发布或复制程序时需要保留对应目录和文件。
- 当前 README 记录的是项目现有实现，PDF 导出效果和 GUI 显示效果仍建议在实际业务 PDF 上进行手工验收。

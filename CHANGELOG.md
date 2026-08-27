# 更新日志

本文档记录当前 Git 工作区相对初始提交 `f3042b0`（2026-08-24，`Initial commit`）的全部未提交更新，包括已跟踪修改和未跟踪新增文件。以下内容是工作区快照，不代表已发布版本。

## V0.42（相对 `v0.41`，2026-08-27）

### 新增功能

- 新增 PDF 与注释表格的双向选中联动：
  - 点击 PDF 注释时，自动选中对应表格记录并将其滚入视图。
  - 点击表格记录时，自动选中对应 PDF 注释并滚动到所在页面。
- 新增“另存 PDF 并 Flatten”功能，默认文件名为 `acrf_flatten.pdf`：
  - 将自由文本注释和页面顶部的 Domain 注释绘制到 PDF 页面内容中。
  - 删除原有 FreeText 注释对象，生成不可编辑的扁平化 PDF。
  - 原有“另存 PDF”继续保留可编辑的 FreeText 注释。
- 注释文本列升级为异步自动完成编辑器，建议项同时展示建议内容和描述，最多返回 50 条匹配结果。
- 新增应用关闭确认流程，可选择“取消”“直接关闭”或“导出并关闭”；导出失败时保持窗口打开并显示错误信息。
- 新增统一消息服务，通过 Toast 反馈 Spec 导入、导出等操作的成功与失败状态。

### 功能改进

- Excel Spec 导入后会批量替换 PDF 注释，仅绘制页码有效且注释文本非空的记录。
- Spec 导出默认文件名加入本地毫秒时间戳：`aCRF-annotations-yyyyMMdd-HHmmss-fff.xlsx`，减少重复导出时的文件名冲突。
- Spec 导出结果细分为成功、取消和失败，关闭应用时可根据结果继续关闭或保留窗口。
- 注释文本改为 `[NOT SUBMITTED]` 时自动启用虚线框，匹配时忽略大小写及前后空格；改为其他文本时不会自动关闭用户手动设置的虚线框。
- 自动完成建议过滤由仅匹配内容改为同时使用建议标题/描述，使描述文字也可参与搜索。
- 注释表格支持扩展单元格选择，自动完成编辑器在失去焦点时提交文本。
- 增加 `DataGrid.ScrollIntoView(object? rowModel)`，供跨视图选中联动时可靠定位表格记录。
- 整行选中时，冻结列与普通列单元格统一显示蓝色选中背景 `#B7DBFF`。
- 改进中文 PDF 注释导出时的字体选择和外观生成处理。

### 问题修复

- 修复 DataGrid 筛选按钮点击后只出现白色窄条、完整筛选弹窗无法显示的问题；筛选 Popup 主题现由应用全局加载。
- 修正筛选“全选”的三态交互和事件路由：
  - 未选或半选时点击后全选。
  - 全选时点击后全不选。
  - 半选状态仅用于展示。
  - 搜索过滤后仅控制当前可见筛选项。
- 修正 AutoComplete 编辑器与单元格编辑态之间的焦点、样式和提交行为。
- 修复 PDF 当前页定位偏差：可见页计算纳入 `PagesPanel` 顶部 16px 间距，页槽高度纳入页面 Host Margin，改善页码显示、滚动、虚拟化和注释定位错页。
- 修复冻结列与非冻结列在整行选中时视觉状态不一致的问题。
- 防止 Dock 窗口关闭事件绕过应用级关闭确认流程。

### 数据与建议系统

- 新增 `Suggestion` 实体和 `suggestion` 数据表，并在应用启动时初始化建议数据。
- 每次启动时清空并重建建议表，确保建议内容与当前内置规则和数据库数据保持一致。
- 建议数据来源扩展为：
  - `template_variable`。
  - `variable_code_list` 与 `code_list_term`。
  - 从 `xxTESTCD = value` 派生的 `xxORRES when xxTESTCD = value` 规则。
- 新增固定建议 `[NOT SUBMITTED]`，描述为 `Not Submitted`。
- 根据 `template_dataset.Class = 'FINDINGS'` 生成 64 个实际 Findings 域规则，并排除 `FINDINGS`、`AP FINDINGS` 分类占位项。规则格式为：
  - 内容：`xxSTAT = NOT DONE when xxTESTCD = xxALL`。
  - 描述：`Not done for all xx test`。
  - 示例：`LBSTAT = NOT DONE when LBTESTCD = LBALL`，描述为 `Not done for all LB test`。
- `CdiscDatasetCatalog` 和 `TemplateVariableDomainCatalog` 改为通过 `IDatabaseService` 访问数据库，不再直接管理 SQLite 连接。

### 界面与交互

- 编辑器工具栏改为卡片化圆角布局，并强化主次操作的按钮视觉层级。
- “另存 PDF”改为拆分按钮，在下拉菜单中提供“另存 PDF 并 Flatten”。
- Dock 内容区改为带边框和圆角的独立面板，统一页面背景、间距和裁剪效果。
- 调整文档标签栏的高度、间距、字体、悬停和激活状态，使当前文档更易识别。
- 注释建议项使用双行展示：上方显示建议内容，下方以蓝色辅助文字显示描述。
- 注释视图会在数据上下文或视觉树连接变化后重新关联表格实例，保证滚动定位和选中联动可用。
- 主窗口新增关闭前导出 Spec 的模态对话框和导出失败提示。

### 工程与依赖

- 引入 `Microsoft.Extensions.DependencyInjection` `10.0.0`，统一注册和解析数据库、消息、建议加载器、目录服务及 ViewModel。
- SQLite 数据访问从手写 `Microsoft.Data.Sqlite` `8.0.1` 迁移到 `SqlSugarCore` `5.1.4.211-preview01`。
- 应用退出时释放依赖注入容器及其服务资源。
- `cdisc_setting.db` 继续使用 `CopyToOutputDirectory=PreserveNewest` 复制到输出目录。
- README 的技术栈说明同步替换为 SqlSugarCore 和 Microsoft.Extensions.DependencyInjection。
- 新增以下工作区文件：
  - `Models/Suggestion.cs`
  - `Services/DatabaseService.cs`
  - `Services/Interface/IDatabaseService.cs`
  - `Services/Interface/IMessageService.cs`
  - `Services/MessageService.cs`
  - `Services/SuggestionOptionsAsyncLoader.cs`


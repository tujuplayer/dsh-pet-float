# 素材来源说明 / Third-party notices

本仓库**不包含任何第三方美术或音频素材**。

## 自带默认素材（CC0 1.0，作者本人声明放弃权利）

| 文件 | 内容 | 生成方式 |
| --- | --- | --- |
| `assets/pet.png` | 默认角色图（蓝色小鱼） | `scripts/gen-assets.py` 用 Pillow 纯代码绘制 |
| `assets/pet.wav` | 捏捏音效（0.18s「啵啾」） | `scripts/gen-assets.py` 程序合成（波形/包络全代码生成） |
| `assets/ah.wav` | 默认「啊」提示音（约 0.55 s，44.1 kHz 单声道） | `scripts/gen-assets.py` 共振峰合成 |

两者由本仓库作者创作并采用 CC0 1.0 通用（公共领域贡献）协议发布，可自由使用、修改、再分发。
想换形象或换声音，直接替换这两个文件即可（也可以用环境变量 `DSH_PET_AVATAR` / `DSH_PET_SOUND` 指到任意路径）。

## 运行时行为

插件在**用户自己的机器上**优先读取：

1. `DSH_PET_AVATAR` / `DSH_PET_SOUND` 环境变量
2. `%USERPROFILE%\.dsh\dsh-pet\avatar.png` / `sound.wav`（用户自备）
3. 包内默认素材（上面两个 CC0 文件）

只有在用户本机已经装了 `dsh-whale-widget-bowl` 且上面都找不到时，才会退化为**本机读取**该插件目录里的角色图/音效（不打包、不再分发，只在本机使用）。因此本仓库的发行物里不含任何第三方素材。

## 伴侣程序

`companion/bin/DshPetFloat.exe` 由 `companion/*.cs` 用 .NET Framework 4.8 的 `csc.exe` 编译，源码在同目录，MIT 协议。

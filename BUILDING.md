# 公開範囲とビルド

GitHubのリポジトリへ入れるもの:

- `src/`（`bin/`・`obj/`を除く共通C#ソースと旧.NET 10プロジェクト）
- `src-net48/`（`bin/`・`obj/`を除く現行.NET Framework 4.8プロジェクト）
- `native/`（C++ソースとヘッダー。模擬BonDriverもテスト用ソースとして含む）
- `assets/`（採用アイコンのPNG・ICO・変換スクリプト・生成プロンプト）
- `build.ps1`、`Publish-Framework48.ps1`、`Publish-Portable.ps1`、`Test-BasicEpg.ps1`
- `.gitignore`、`README.md`、`BUILDING.md`、`LICENSE`、`THIRD-PARTY-NOTICES.txt`

配布用ZIPはソースのリポジトリに入れず、GitHub Releasesへ添付する。
既存の.gitignoreで、個人設定・受信dat・BonDriverを含む実行用フォルダー、ログ、検証結果、ビルド生成物は対象外にする。
実チューナー用のBonDriver DLL・ini・ChSet4・EPG datを新たにリポジトリへコピーしないこと。
自作部分はMIT License（`LICENSE`）。EDCB由来部分（EpgDataCap3.dll・BonDriverインターフェース定義）はEDCBの条件に従うため、一括でMITと表示しない。

## 必要な開発環境

- Windows
- .NET SDK 10（実行用ランタイムだけではビルド不可）
- Visual Studio / Build Toolsの「C++によるデスクトップ開発」
- MSVC v143 x86/x64ビルドツールとWindows SDK
- .NET Framework 4.8開発用ターゲティングパック
- Git、PowerShell、初回NuGet復元用のネット接続

ICOを変更しない通常ビルドにPythonは不要。アイコン再変換時だけPythonとPillowを使用する。

## EDCB参照ソースの配置

既存スクリプトとC++のincludeは、リポジトリの隣の`EpgCollector-reference`を参照する。
EDCBソースを本リポジトリへ丸ごと追加する必要はない。

```text
作業フォルダー/
  EDCBepgEx/                 ← このリポジトリ
    build.ps1
    src/
    src-net48/
    native/
  EpgCollector-reference/    ← xtne6f/EDCB
```

作業フォルダーで実行:

```powershell
git clone https://github.com/xtne6f/EDCB.git EpgCollector-reference
git -C EpgCollector-reference checkout e6e3c614d0a8b287b0072e084f509494cd203e7e
Set-Location .\EDCBepgEx
```

## 現行4.8版のビルド

```powershell
.\build.ps1
```

Visual Studioの場所は`vswhere`で自動検出する。C++ツールセットはv143があればv143、なければ最新を使う。明示する場合:

```powershell
.\build.ps1 -VsRoot 'C:\Program Files\Microsoft Visual Studio\2022\Community' -PlatformToolset v143
```

スクリプトはx86/x64の解析DLL・受信ワーカー・テスト用BonDriverをビルドした後、4.8版のGUIと配布ZIPを作る。

- 実行ファイル: `framework48\EDCBepgEx.exe`
- 配布用ZIP: `EDCBepgEx-dev-<コミット>.zip`

バージョンはリリース時だけ`-Version 1.2.3`で付ける。その場合ZIPは`EDCBepgEx-1.2.3.zip`、exeのバージョンも1.2.3になる。指定しないビルドは開発版（exeのバージョンは0.0.0-dev）。

## GitHub Actions

`.github/workflows/build.yml`がpush・PRごとにビルドし、`EDCBepgEx-dev-<コミット>.zip`をActionsの成果物としてそのまま保存する（ZIPの二重化なし）。EDCBのソースは上記のコミットを自動で取得する。
`v1.2.3`形式のタグをpushすると、そのバージョンでビルドしてGitHub Releasesに添付する。
サンプルに依存するテストはActionsでは実行しない。

ネイティブ部分をビルド済みで、GUIやREADMEの更新だけを配布へ反映する場合:

```powershell
.\Publish-Framework48.ps1
```

配布ZIPに個人のconfig.json、模擬BonDriver、ログ、受信データは含めない。
EDCBやProxyExの稼働中フォルダーをビルド用に変更する必要はない。

## テスト

テストは元の提供サンプルを`../EDCB`に置く開発環境向け。公開ソースだけではサンプル依存テストは実行できない。
通常のビルド・アプリ起動にはサンプル不要。テストは実チューナーを開かず、模擬BonDriverとdatの再生を利用する。

```powershell
.\framework48\EDCBepgEx.exe --self-test
.\framework48\EDCBepgEx.exe --quick-integration-test
.\framework48\EDCBepgEx.exe --open-failure-test
.\framework48\EDCBepgEx.exe --fault-test
```

これらはGUIプログラムのため、PowerShellから終了コードまで待つ場合は`Start-Process -Wait -PassThru`を使用する。

- `--self-test`: オフラインのデータ・保存・通信テスト
- `--integration-test`: 模擬BonDriverで2本並列取得、キャンセル、A・Bそれぞれ2本の並列取得。最短60秒の動作も確認するため約2分かかる
- `--quick-integration-test`: 待ち時間を短縮した内部テスト用の統合テスト（通常の取得設定には影響しない）
- `--fault-test`: 模擬BonDriverの受信TSを途中で変更し、既存datが保護されることを確認
- `--open-failure-test`: BonDriverオープン失敗と応答停止時の終了・他Bonの継続を確認
- `Test-BasicEpg.ps1`: 地デジのdatを再生し、基本情報のみ取得モードのワーカー引数と完了判定を確認

dat履歴のテストでは、同一サービスの旧データにだけある番組・新規番組・開始時刻が更新された共通番組を合成し、x86/x64のEDCB解析DLLで旧番組の保持と更新反映を確認する。読み込み順はEpgTimerSrvの先頭PAT・先頭TOT先読みを再現している。

## ソース構成

- `src/`: Windows Forms GUI、スケジュール、並列実行、datの保存・EDCB通信の共通C#ソース。`EpgCollector.csproj`は旧.NET 10版
- `src-net48/`: 現行の.NET Framework 4.8版プロジェクト（`src/*.cs`を共有）
- `native/worker.cpp`: C++17の受信・EPG解析ワーカー。x86/x64それぞれビルドする
- `native/DropCounter.h`: 連続性カウンターのDrop計数
- `native/MockBonDriver.cpp`: オフライン統合テスト専用の模擬BonDriver（ネットワーク・チューナー接続なし）
- `assets/`: アイコン。`Build-Icon.py`で元PNGから各サイズPNGとICOを生成する（要Pillow）
- `Publish-Portable.ps1`: 旧.NET 10の自己完結版を作る。`build.ps1 -Framework net10.0-windows`で事前にビルドが必要

参照ソースは`EpgCollector-reference`（xtne6f/EDCB、`e6e3c614d0a8b287b0072e084f509494cd203e7e`）と、設計確認に使った`ProxyEx-reference`（stuayu/BonDriverProxyEx、`a63829562d5b082fe820a40b1d20656b9ca9cc85`）。ビルドに必要なのはEDCBのみ。参照ソース自体は変更しない。

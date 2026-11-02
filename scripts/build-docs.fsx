// API ドキュメントとサイトを fsdocs で生成する。
//
//     dotnet tool restore
//     dotnet fsi scripts/build-docs.fsx            output/ に生成する
//     dotnet fsi scripts/build-docs.fsx --watch    生成して http://localhost:8901 で表示する
//
// README とドキュメントは、リポジトリの中で読めるように、ファイルへの相対リンクと GitHub 式のアンカーで書いている。
// そのままサイトにするとリンクが切れるので、入力を作業用のフォルダーへ写して書き換えてから fsdocs に渡す。
// 書き換えるのは作業用の写しだけで、元のファイルには触れない。
//
// fsdocs はビルド結果の DLL を読むだけでビルドはしないので、先に fsdocs が見る既定の構成でプロジェクトをビルドする。

open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let input = Path.Combine(root, "tmp", "docs-input")

let projects =
    [ "src/EffectSystem/EffectSystem.fsproj"
      "src/EffectSystem.Plus/EffectSystem.Plus.fsproj"
      "samples/EffectSystem.Samples/EffectSystem.Samples.fsproj"
      "samples/EffectSystem.PriorArt/EffectSystem.PriorArt.fsproj" ]

/// サイトに載せるページ。リポジトリ内のパス、サイトでのファイル名、メニューの順序。
let pages =
    [ "README.md", "index.md", 1
      "docs/porting-notes.md", "porting-notes.md", 2
      "docs/row-polymorphism-report.md", "row-polymorphism-report.md", 3
      "docs/zio-report.md", "zio-report.md", 4 ]

/// ソースへのリンクの行き先。ソースはサイトにないので、対応する API ドキュメントのページへ向ける。
let apiPage (path: string) =
    match path with
    | "src/EffectSystem/Core.fs" -> Some "reference/effectsystem.html"
    | "src/EffectSystem/Direct.fs" -> Some "reference/effectsystem-direct.html"
    | "src/EffectSystem/Freer.fs" -> Some "reference/effectsystem-freer.html"
    | "src/EffectSystem/Capability.fs" -> Some "reference/effectsystem-capability.html"
    | "src/EffectSystem.Plus/" -> Some "reference/effectsystem-plus.html"
    | "src/EffectSystem.Plus/RunStyle.fs" -> Some "reference/effectsystem-plus-runstyle.html"
    | "samples/EffectSystem.Samples/" -> Some "reference/effectsystem-samples.html"
    | p when p.StartsWith "samples/EffectSystem.Samples/" && p.EndsWith ".fs" ->
        let moduleName = Path.GetFileNameWithoutExtension(p).ToLowerInvariant()
        Some $"reference/effectsystem-samples-{moduleName}.html"
    | "samples/EffectSystem.PriorArt/" -> Some "reference/effectsystem-priorart.html"
    | p when p.StartsWith "samples/EffectSystem.PriorArt/" && p.EndsWith ".fs" ->
        let moduleName = Path.GetFileNameWithoutExtension(p).ToLowerInvariant()
        Some $"reference/effectsystem-priorart-{moduleName}.html"
    | _ -> None

/// コードブロックの外の見出しを取り出す。
let headings (text: string) =
    let mutable inFence = false

    [ for line in text.Split('\n') do
          if line.StartsWith "```" then
              inFence <- not inFence
          elif not inFence && Regex.IsMatch(line, @"^#{1,6} ") then
              yield line.TrimStart('#').Trim() ]

/// GitHub と VS Code のアンカーの規則。
let githubSlug (heading: string) =
    let plain = heading.Replace("`", "").ToLowerInvariant()
    Regex.Replace(plain, @"[^\p{L}\p{M}\p{N}\s\-_]", "").Replace(" ", "-")

/// fsdocs のアンカーの規則。インラインコードはアンカーに含まれない。
let fsdocsSlug (heading: string) =
    Regex.Replace(Regex.Replace(heading, "`[^`]*`", ""), @"[^\p{L}\p{M}\p{N}]+", "-").Trim('-')

/// ページごとの、GitHub のアンカーから fsdocs のアンカーへの対応。
let anchors =
    dict
        [ for source, _, _ in pages do
              let text = File.ReadAllText(Path.Combine(root, source))
              source, dict [ for h in headings text -> githubSlug h, fsdocsSlug h ] ]

/// 元のファイルから見た相対パスを、リポジトリのルートからのパスに直す。
let fromRoot (source: string) (target: string) =
    let directory = Path.GetDirectoryName(Path.Combine(root, source))
    let full = Path.GetFullPath(Path.Combine(directory, target))
    let relative = Path.GetRelativePath(root, full).Replace('\\', '/')
    if target.EndsWith "/" then relative.TrimEnd('/') + "/" else relative

/// リンク先をサイトでの行き先に直す。サイトに対応するページがなければ None を返す。
let rewriteTarget (source: string) (target: string) =
    if Regex.IsMatch(target, @"^[a-zA-Z][a-zA-Z0-9+.-]*:") then
        Some target
    else
        let path, fragment =
            match target.IndexOf '#' with
            | -1 -> target, ""
            | i -> target.Substring(0, i), target.Substring(i + 1)

        let targetSource = if path = "" then source else fromRoot source path

        let site =
            match pages |> List.tryFind (fun (s, _, _) -> s = targetSource) with
            | Some(_, file, _) -> Some(Path.ChangeExtension(file, ".html"))
            | None when targetSource.StartsWith "docs/figures/" && targetSource.EndsWith ".svg" ->
                Some(targetSource.Substring 5)
            | None -> apiPage targetSource

        let anchor =
            if fragment = "" then
                ""
            else
                match anchors.TryGetValue targetSource with
                | true, map when map.ContainsKey fragment -> "#" + map[fragment]
                | _ -> failwith $"{source}: アンカーが見つからない: {target}"

        if path = "" then
            Some anchor
        else
            site |> Option.map (fun s -> s + anchor)

/// コードブロックの外のリンクを書き換える。行き先がないリンクは文字だけ残す。
let rewriteLinks (source: string) (text: string) =
    let rewriteLine (line: string) =
        let inner =
            Regex.Replace(
                line,
                @"(!?)\[([^\[\]]*)\]\(([^)\s]+)\)",
                fun m ->
                    match rewriteTarget source m.Groups[3].Value with
                    | Some t -> $"{m.Groups[1].Value}[{m.Groups[2].Value}]({t})"
                    | None -> m.Groups[2].Value
            )

        // 画像を包むリンク [![図](a.svg)](a.svg) の外側。
        Regex.Replace(
            inner,
            @"\)\]\(([^)\s]+)\)",
            fun m ->
                match rewriteTarget source m.Groups[1].Value with
                | Some t -> $")]({t})"
                | None -> failwith $"{source}: 画像のリンク先がない: {m.Value}"
        )

    let mutable inFence = false

    text.Split('\n')
    |> Array.map (fun line ->
        if line.StartsWith "```" then
            inFence <- not inFence
            line
        elif inFence then
            line
        else
            rewriteLine line)
    |> String.concat "\n"

let prepareInput () =
    if Directory.Exists input then
        Directory.Delete(input, true)

    Directory.CreateDirectory input |> ignore

    // 図とテンプレートはそのまま写す。
    File.Copy(Path.Combine(root, "docs", "_template.html"), Path.Combine(input, "_template.html"))
    Directory.CreateDirectory(Path.Combine(input, "figures")) |> ignore

    for figure in Directory.GetFiles(Path.Combine(root, "docs", "figures")) do
        File.Copy(figure, Path.Combine(input, "figures", Path.GetFileName figure))

    for source, file, index in pages do
        let text = File.ReadAllText(Path.Combine(root, source)).Replace("\r\n", "\n")
        let frontMatter = $"---\ncategory: ドキュメント\ncategoryindex: 1\nindex: {index}\n---\n\n"
        File.WriteAllText(Path.Combine(input, file), frontMatter + rewriteLinks source text)

let run (arguments: string list) =
    let info = ProcessStartInfo("dotnet", arguments, WorkingDirectory = root, UseShellExecute = false)
    use p = Process.Start info
    p.WaitForExit()
    p.ExitCode

let common =
    [ "--input"; input; "--nonpublic"; "--strict"; "--projects" ]
    @ projects
    @ [ "--parameters"; "fsdocs-collection-name"; "EffectSystem" ]

prepareInput ()

// ビルドに失敗したら、その終了コードで止める。
projects
|> List.tryPick (fun project ->
    match run [ "build"; project ] with
    | 0 -> None
    | code -> Some code)
|> Option.iter exit

let watch = fsi.CommandLineArgs |> Array.contains "--watch"

let code =
    if watch then
        run ([ "fsdocs"; "watch"; "--nolaunch" ] @ common)
    else
        run ([ "fsdocs"; "build"; "--clean"; "--output"; "output" ] @ common @ [ "root"; "/" ])

exit code

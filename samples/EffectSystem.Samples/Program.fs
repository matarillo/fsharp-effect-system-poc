/// <summary>サンプルの実行とコマンドライン。</summary>
/// <category index="5">計測と実行</category>
module EffectSystem.Samples.Program

open EffectSystem.Samples

/// <summary>見出しを付けて 1 つの例を実行する。</summary>
let private section (title: string) (f: Effects.Log -> unit) =
    printfn "\n=== %s ===" title
    f (printfn "%s")

/// <summary>例外で終わる例を実行し、例外の名前を出力する。</summary>
let private attempt (title: string) (f: Effects.Log -> unit) =
    section title (fun out ->
        try
            f out
        with e ->
            out $"!! {e.GetType().Name}")

/// <summary>記事とマニュアルの例をすべて実行する。</summary>
let private showAll () =
    section "Koka: ask-emit" KokaStyle.exampleAskEmit
    section "Koka: ask を 2 回再開" KokaStyle.exampleAskTwice
    section "Koka: emit を 2 回再開" KokaStyle.exampleEmitTwice
    section "Koka: 再開可能な例外" KokaStyle.exampleSubtractNaturalNumber

    section "Effekt: askEmit" EffektStyle.exampleAskEmit
    section "Effekt: 両方を 2 回再開" EffektStyle.exampleBothTwice
    section "Effekt: インターフェイス制約を行として使う" EffektStyle.exampleAskEmitRow
    section "Effekt: 行の一部を途中で消す" (fun out -> out (string (EffektStyle.exampleLocalAsk out)))
    section "Effekt: 能力の寿命" EffektStyle.exampleScoped
    attempt "Effekt: 能力の構築の途中の失敗" EffektStyle.exampleScopedFailure
    section "Effekt: 字句スコープ" (fun out -> out (string (EffektStyle.exampleLexicalAddressing ())))
    attempt "Effekt: 持ち出した能力" (fun out -> out (EffektStyle.exampleEscapedCapability ()))

    section "Unison: emitAsk" UnisonStyle.exampleEmitAsk
    section "Unison: ask を 2 回再開" UnisonStyle.exampleAskTwice

    section "OCaml: ask_emit" OCamlStyle.exampleAskEmit
    attempt "OCaml: ハンドラーの付け忘れ" OCamlStyle.exampleMissingHandler
    attempt "OCaml: 2 回再開" OCamlStyle.exampleResumeTwice
    section "OCaml: State (Shallow)" OCamlStyle.exampleState

    section "Polysemy: askEmit" PolysemyStyle.exampleAskEmit
    section "PureScript Run + FSharpPlus: askEmit" PureScriptStyle.exampleAskEmit
    section "PureScript Run + FSharpPlus: next を 2 回呼ぶ" PureScriptStyle.exampleAskTwice
    section "PureScript Run + FSharpPlus: next を呼ばない" PureScriptStyle.exampleAskAbort

    section "OCaml manual 24.1" (fun out -> out (string (OCamlManual.basics ())))
    section "OCaml manual 24.2" (fun out -> out (string (OCamlManual.concurrency ())))
    section "OCaml manual 24.3" OCamlManual.userLevelThreads
    section "OCaml manual 24.3 discontinue" OCamlManual.resumingWithAnException
    section "OCaml manual 24.4" (fun out -> out (string (OCamlManual.controlInversionList ())))
    section "OCaml manual 24.5" (fun out -> out (OCamlManual.baz ()))
    section "OCaml manual 24.6" OCamlManual.shallowFollowsProtocol
    attempt "OCaml manual 24.6 違反" OCamlManual.shallowViolatesProtocol

    section "Async one-shot: 24.3" AsyncOneShot.userLevelThreads
    section "Async one-shot: discontinue" AsyncOneShot.resumingWithAnException

    section "静的な行: askEmit" StaticRow.exampleAskEmit

    section "レコードの環境: askEmit" RecordEnv.exampleAskEmit
    section "レコードの環境: 名前付きレコードで閉じる" RecordEnv.exampleClosedEnv
    section "レコードの環境: ラベルで分ける" (fun out -> out (string (RecordEnv.exampleTwoLabels ())))
    section "レコードの環境: 能力の引数をまとめる" RecordEnv.exampleActivePattern

    section "State × NonDet" (fun out ->
        out $"State が内側: %A{Advanced.stateInsideNonDet ()}"
        out $"State が外側: %A{Advanced.stateOutsideNonDet ()}")

    section "トランザクション: commit" Advanced.transactionCommits
    section "トランザクション: 重ねる" Advanced.transactionNested
    attempt "トランザクション: rollback" Advanced.transactionRollsBack

/// <summary>エントリーポイント。</summary>
/// <remarks>
/// 引数なしですべての例を実行する。<c>bench</c> でコストを測り、<c>limit エンジン名 回数</c> でスタックの限界を調べる。
/// </remarks>
/// <param name="argv">コマンドライン引数。</param>
[<EntryPoint>]
let main argv =
    match argv with
    | [| "bench" |] -> Bench.run ()
    | [| "limit"; engine; n |] -> Bench.limit engine (int n)
    | _ -> showAll ()

    0

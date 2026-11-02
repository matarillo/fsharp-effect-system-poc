/// <summary>サンプルの実行とコマンドライン。</summary>
/// <category index="3">計測と実行</category>
module EffectSystem.PriorArt.Program

/// <summary>出力先。<c>EffectSystem.Samples</c> を開くと同じ名前の <c>Bench</c> モジュールの関数が混ざるので、開かずに型だけを借りる。</summary>
type private Log = EffectSystem.Samples.Effects.Log

/// <summary>見出しを付けて 1 つの例を実行する。</summary>
let private section (title: string) (f: Log -> unit) =
    printfn "\n=== %s ===" title
    f (printfn "%s")

/// <summary>例をすべて実行する。</summary>
let private showAll () =
    section "Orsak: askEmit" OrsakStyle.exampleAskEmit
    section "Orsak: トランザクション: commit" OrsakStyle.transactionCommits
    section "Orsak: トランザクション: 失敗" OrsakStyle.transactionFails
    section "Orsak: and! で失敗を合わせる" OrsakStyle.exampleCheckLine

    section "FIO: askEmit" FioStyle.exampleAskEmit
    section "FIO: トランザクション: commit" FioStyle.transactionCommits
    section "FIO: トランザクション: 失敗" FioStyle.transactionFails
    section "FIO: トランザクション: 中断" FioStyle.transactionInterrupted
    section "FIO: 親が終わると子は中断される" FioStyle.exampleChildInterrupted
    section "FIO: 中断不可の領域" FioStyle.exampleUninterruptible

/// <summary>エントリーポイント。</summary>
/// <remarks>
/// 引数なしですべての例を実行する。<c>bench</c> でコストを測り、<c>parked fio|orsak 個数</c> で止まっている計算のメモリを、
/// <c>limit エンジン名 回数</c> でスタックの限界を調べる。
/// </remarks>
/// <param name="argv">コマンドライン引数。</param>
[<EntryPoint>]
let main argv =
    match argv with
    | [| "bench" |] -> Bench.run ()
    | [| "parked"; library; n |] -> Bench.parked library (int n)
    | [| "limit"; engine; n |] -> Bench.limit engine (int n)
    | _ -> showAll ()

    0

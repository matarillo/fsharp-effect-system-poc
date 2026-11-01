/// <summary><c>Async.FromContinuations</c> が渡す <c>(ok, err, cancel)</c> を、そのまま 1 回限りの継続として使う。</summary>
/// <remarks>
/// <para>
/// F# の <c>Async</c> は、もともと継続の仕組みを持っている。<c>ok</c> が OCaml の <c>continue</c>、
/// <c>err</c> が <c>discontinue</c> に当たり、2 回目の呼び出しは FSharp.Core が検査して例外にする。
/// FSharp.Core だけで書け、タスクごとにスレッドを消費しない。その代わり計算は <c>async { }</c> で色付けされる。
/// </para>
/// <para>
/// 操作は型で探さず、<see cref="T:EffectSystem.Samples.AsyncOneShot.Scheduler"/> のメソッドとして直接呼ぶ。
/// スコープの仕組みは能力渡しと同じで、スケジューラーを持っていなければ操作を呼べない。
/// </para>
/// </remarks>
/// <category index="4">発展</category>
module EffectSystem.Samples.AsyncOneShot

open System.Collections.Generic
open System.Threading
open EffectSystem.Samples.Effects

/// <summary>取り出した継続。OCaml の <c>continue</c> と <c>discontinue</c> の組に当たる。</summary>
/// <typeparam name="'a">再開するときに渡す値の型。</typeparam>
type Continuation<'a> =
    { /// <summary>値で再開する。</summary>
      Resume: 'a -> unit
      /// <summary>例外で再開する。中断している位置で例外が発生し、<c>finally</c> が走る。</summary>
      Discontinue: exn -> unit }

/// <summary>中断して継続を取り出す。2 回目の再開は FSharp.Core が <c>InvalidOperationException</c> にする。</summary>
/// <param name="f">継続を受け取る関数。保存しておいて後で再開してよい。</param>
let capture (f: Continuation<'a> -> unit) : Async<'a> =
    Async.FromContinuations(fun (ok, err, _) -> f { Resume = ok; Discontinue = err })

/// <summary>交換の相手がいないまま止まったタスクへ届ける例外。</summary>
exception ImproperSynchronization

/// <summary>マニュアル 24.3 のラウンドロビンのスケジューラー。<c>perform</c> の代わりにメソッドを直接呼ぶ。</summary>
/// <remarks>
/// キューには継続の再開を入れる。止まったタスクは継続として保存されているだけで、スレッドを占有しない。
/// </remarks>
/// <param name="out">タスクの例外の出力先。</param>
type Scheduler(out: Log) =
    let runQ = Queue<unit -> unit>()
    let mutable exchanger: (int * Continuation<int>) option = None

    let start (task: Async<unit>) =
        Async.StartWithContinuations(task, ignore, (fun e -> out (e.GetType().Name)), ignore)

    /// <summary>他のタスクへ実行を譲る。</summary>
    member _.Yield() : Async<unit> =
        capture (fun k -> runQ.Enqueue(fun () -> k.Resume()))

    /// <summary>新しいタスクをすぐに起動し、今のタスクをキューへ入れる。</summary>
    member _.Fork(task: Async<unit>) : Async<unit> =
        capture (fun k ->
            runQ.Enqueue(fun () -> k.Resume())
            start task)

    /// <summary>他のタスクと値を交換する。相手がいなければ止まって待つ。</summary>
    member _.Xchg(n: int) : Async<int> =
        capture (fun k ->
            match exchanger with
            | Some(n', k') ->
                exchanger <- None
                runQ.Enqueue(fun () -> k'.Resume n)
                k.Resume n'
            | None -> exchanger <- Some(n, k))

    /// <summary>キューが空になるまでタスクを動かす。最後まで相手のいないタスクには例外を届ける。</summary>
    /// <remarks>
    /// <c>Async</c> の継続は、<c>Async</c> の実行中のスレッドから呼ぶとスレッドプールへ回されて順序が崩れる。
    /// そうならないようキューを専用のスレッドで回す。
    /// </remarks>
    member _.Run(main: Async<unit>) : unit =
        let loop () =
            start main

            while runQ.Count > 0 || exchanger.IsSome do
                if runQ.Count > 0 then
                    runQ.Dequeue () ()
                else
                    let (_, k) = exchanger.Value
                    exchanger <- None
                    k.Discontinue ImproperSynchronization

        let thread = Thread(loop)
        thread.Start()
        thread.Join()

/// <summary>2 つのタスクが値を交換する。出力の順序はマニュアル 24.3 と同じである。</summary>
/// <param name="out">出力先。</param>
let userLevelThreads (out: Log) : unit =
    let s = Scheduler(out)

    s.Run(
        async {
            do!
                s.Fork(
                    async {
                        out "[t1] Sending 0"
                        let! v = s.Xchg 0
                        out $"[t1] received {v}"
                    }
                )

            do!
                s.Fork(
                    async {
                        out "[t2] Sending 1"
                        let! v = s.Xchg 1
                        out $"[t2] received {v}"
                    }
                )
        }
    )

/// <summary>相手のいない <c>Xchg</c> で止まったタスクにも、例外での再開によって <c>finally</c> が走る。</summary>
/// <param name="out">出力先。</param>
let resumingWithAnException (out: Log) : unit =
    let s = Scheduler(out)

    s.Run(
        async {
            try
                out "open"
                let! v = s.Xchg 0
                out $"received {v}"
            finally
                out "close"
        }
    )

/// <summary>継続を 2 回呼ぶ。FSharp.Core の検査で <c>InvalidOperationException</c> になる。</summary>
let resumeTwice () : unit =
    let saved = ref None
    Async.StartImmediate(capture (fun k -> saved.Value <- Some k))
    let k: Continuation<unit> = saved.Value.Value
    k.Resume()
    k.Resume()

/// <summary>OCaml マニュアル 12.24 Effect handlers の例を、節の順に Direct エンジンで写す。</summary>
/// <remarks>
/// 普通の関数のまま <c>perform</c> できるので、計算の部分はほぼ同じ形になる。
/// 違いが出るのはハンドラーの書き方で、OCaml の <c>effect</c> パターンは <c>Deep.ctl</c> の分岐になる。
/// 実行結果はマニュアルの REPL の結果をテストの期待値にしている。
/// </remarks>
/// <category index="3">OCaml マニュアル</category>
module EffectSystem.Samples.OCamlManual

open System.Collections.Generic
open EffectSystem
open EffectSystem.Direct
open EffectSystem.Samples.Effects

// ---- 24.1 Basics ----

/// <summary>整数を差し出し、整数を受け取る操作。OCaml の <c>type _ Effect.t += Xchg: int -> int t</c> に当たる。</summary>
type Xchg =
    /// <summary>差し出す整数を渡す。</summary>
    | Xchg of int

    interface Effect<int>

/// <summary>2 回交換して和を返す計算。式の途中に <c>perform</c> を書ける。</summary>
let comp1 () = perform (Xchg 0) + perform (Xchg 1)

/// <summary>24.1 差し出された値の次の値で答えるハンドラーを被せる。結果は 3 になる。</summary>
/// <remarks>OCaml の <c>try comp1 () with | effect (Xchg n), k -> continue k (n+1)</c> に当たる。</remarks>
let basics () : int =
    Deep.tryWith comp1 [ Deep.ctl (fun (Xchg n) k -> Deep.resume k (n + 1)) ]

// ---- 24.2 Concurrency ----

/// <summary>タスクの状態。完了しているか、差し出す値と継続を持って止まっている。</summary>
/// <typeparam name="'a">タスクの結果の型。</typeparam>
/// <remarks>継続を値としてデータ構造に保存できることが、並行処理の土台になる。</remarks>
type Status<'a> =
    /// <summary>タスクは完了した。</summary>
    | Complete of 'a
    /// <summary>タスクは値を差し出して止まっている。</summary>
    | Suspended of msg: int * cont: Deep.Continuation<int, Status<'a>>

/// <summary>計算を、完了するか次に止まるまで進める。継続をすぐには再開せず、状態として返す。</summary>
/// <remarks>
/// <code lang="ocaml"><![CDATA[
/// let step (f : unit -> 'a) () : 'a status =
///   match f () with
///   | v -> Complete v
///   | effect (Xchg msg), cont -> Suspended {msg; cont}
/// ]]></code>
/// </remarks>
/// <param name="f">実行する計算。</param>
let step (f: unit -> 'a) () : Status<'a> =
    Deep.matchWith
        f
        { retc = Complete
          exnc = rethrow
          effc = [ Deep.ctl (fun (Xchg msg) cont -> Suspended(msg, cont)) ] }

/// <summary>2 つのタスクを交互に進め、差し出された値を交換させる。OCaml の <c>run_both</c> に当たる。</summary>
/// <param name="a">1 つ目のタスク。</param>
/// <param name="b">2 つ目のタスク。</param>
let rec runBoth (a: unit -> Status<'a>) (b: unit -> Status<'b>) : 'a * 'b =
    match a (), b () with
    | Complete va, Complete vb -> (va, vb)
    | Suspended(m1, k1), Suspended(m2, k2) -> runBoth (fun () -> Deep.resume k1 m2) (fun () -> Deep.resume k2 m1)
    | _ -> failwith "Improper synchronization"

/// <summary>2 回交換して積を返す計算。</summary>
let comp2 () = perform (Xchg 21) * perform (Xchg 21)

/// <summary>24.2 2 つの計算の間でメッセージを交換する。結果は <c>(42, 0)</c> になる。</summary>
let concurrency () : int * int = runBoth (step comp1) (step comp2)

// ---- 24.3 User-level threads ----

/// <summary>新しいタスクを起動する操作。OCaml の <c>Fork : (unit -> unit) -> unit t</c> に当たる。</summary>
type Fork =
    /// <summary>起動するタスクを渡す。</summary>
    | Fork of (unit -> unit)

    interface Effect<unit>

/// <summary>他のタスクへ実行を譲る操作。OCaml の <c>Yield : unit t</c> に当たる。</summary>
type Yield =
    /// <summary>実行を譲る。</summary>
    | Yield

    interface Effect<unit>

/// <summary>タスクを起動する。</summary>
let fork f = perform (Fork f)

/// <summary>実行を譲る。<c>yield</c> は F# のキーワードなので、<c>yield'</c> としている。</summary>
let yield' () = perform Yield

/// <summary>他のタスクと値を交換する。</summary>
let xchg v = perform (Xchg v)

/// <summary>交換の相手がいないまま止まったタスクへ届ける例外。OCaml の <c>Improper_synchronization</c> に当たる。</summary>
exception ImproperSynchronization

/// <summary>ラウンドロビンのスケジューラー。OCaml の <c>run</c> に当たる。</summary>
/// <remarks>
/// <para>
/// スレッドの切り替えをライブラリの中で実装できることを示す例である。タスクのコードにハンドラーは現れない。
/// キューには継続の再開を入れる。止まったタスクは、継続としてデータ構造に保存されているだけである。
/// </para>
/// <para>
/// キューが空になった時点で交換の相手を待っているタスクがあれば、<c>Deep.discontinue</c> で例外を届ける。
/// マニュアルの「Resuming with an exception」の節の版である。
/// </para>
/// </remarks>
/// <param name="out">出力先。タスクの例外もここへ出す。</param>
/// <param name="main">最初のタスク。</param>
let run (out: Log) (main: unit -> unit) : unit =
    let exchanger: (int * Deep.Continuation<int, unit>) option ref = ref None
    let runQ = Queue<unit -> unit>()

    let enqueue (k: Deep.Continuation<'x, unit>) (v: 'x) =
        runQ.Enqueue(fun () -> Deep.resume k v)

    let dequeue () =
        if runQ.Count = 0 then
            match exchanger.Value with
            | None -> ()
            | Some(_, k) ->
                exchanger.Value <- None
                Deep.discontinue k ImproperSynchronization
        else
            runQ.Dequeue () ()

    let rec spawn (f: unit -> unit) : unit =
        Deep.matchWith
            f
            { retc = dequeue
              exnc =
                fun e ->
                    out (e.GetType().Name)
                    dequeue ()
              effc =
                [ Deep.ctl (fun (_: Yield) k ->
                      enqueue k ()
                      dequeue ())
                  Deep.ctl (fun (Fork f) k ->
                      enqueue k ()
                      spawn f)
                  Deep.ctl (fun (Xchg n) k ->
                      match exchanger.Value with
                      | Some(n', k') ->
                          exchanger.Value <- None
                          enqueue k' n
                          Deep.resume k n'
                      | None ->
                          exchanger.Value <- Some(n, k)
                          dequeue ()) ] }

    spawn main

/// <summary>24.3 2 つのタスクが値を交換する。</summary>
/// <remarks>
/// <code lang="text">
/// [t1] Sending 0
/// [t2] Sending 1
/// [t2] received 0
/// [t1] received 1
/// </code>
/// </remarks>
/// <param name="out">出力先。</param>
let userLevelThreads (out: Log) : unit =
    run out (fun () ->
        fork (fun () ->
            out "[t1] Sending 0"
            let v = xchg 0
            out $"[t1] received {v}")

        fork (fun () ->
            out "[t2] Sending 1"
            let v = xchg 1
            out $"[t2] received {v}"))

/// <summary>24.3 相手のいない <c>xchg</c> で止まったタスクも、<c>discontinue</c> により <c>finally</c> が走る。</summary>
/// <param name="out">出力先。</param>
let resumingWithAnException (out: Log) : unit =
    run out (fun () ->
        fork (fun () ->
            try
                out "open"
                out $"received {xchg 0}"
            finally
                out "close"))

// ---- 24.4 Control inversion ----

/// <summary>消費者が 1 つずつ取り出す列の節。OCaml の <c>Seq.node</c> に当たる。</summary>
/// <typeparam name="'a">要素の型。</typeparam>
type Node<'a> =
    /// <summary>列の終わり。</summary>
    | Nil
    /// <summary>要素と、残りの列を取り出す関数。この関数の実体は継続の再開である。</summary>
    | Cons of 'a * (unit -> Node<'a>)

/// <summary>要素を 1 つ差し出す操作。マニュアルの <c>M.Yield</c> に当たる。</summary>
/// <typeparam name="'a">要素の型。</typeparam>
/// <remarks>
/// マニュアルでは呼び出しごとに局所モジュールで新しい操作を作るが、F# の型は局所的に作れないので共通の型にしている。
/// この違いは <c>nestedInvertSameType</c> で現れる。
/// </remarks>
type Next<'a> =
    /// <summary>要素を渡す。</summary>
    | Next of 'a

    interface Effect<unit>

/// <summary>push 型の <c>iter</c> を pull 型の列へ変換する。</summary>
/// <remarks>
/// 継続を <c>Cons</c> の中に保存し、消費者が次の要素を求めたときに再開する。継続は 1 回限りなので、列は使い捨てになる。
/// <code lang="ocaml"><![CDATA[
/// let invert (type a) ~(iter : (a -> unit) -> unit) : a Seq.t =
///   ...
///   fun () -> match iter yield with
///     | () -> Seq.Nil
///     | effect M.Yield v, k -> Seq.Cons (v, continue k)
/// ]]></code>
/// </remarks>
/// <param name="iter">要素を押し出す関数。</param>
let invert (iter: ('a -> unit) -> unit) : unit -> Node<'a> =
    let yield'' (v: 'a) = perform (Next v)

    fun () ->
        Deep.matchWith
            (fun () -> iter yield'')
            { retc = fun () -> Nil
              exnc = rethrow
              effc = [ Deep.ctl (fun (Next(v: 'a)) k -> Cons(v, (fun () -> Deep.resume k ()))) ] }

/// <summary>列を最後まで取り出してリストにする。</summary>
/// <param name="node">列の先頭を返す関数。</param>
let rec toList (node: unit -> Node<'a>) : 'a list =
    match node () with
    | Nil -> []
    | Cons(v, rest) -> v :: toList rest

/// <summary>24.4 <c>List.iter</c> を列へ変換する。</summary>
let controlInversionList () : int list =
    toList (invert (fun push -> List.iter push [ 1; 2; 3 ]))

/// <summary>24.4 同じ <c>invert</c> を <c>String.iter</c> にも使う。</summary>
let controlInversionString () : char list =
    toList (invert (fun push -> String.iter push "OCaml"))

/// <summary>同じ末尾を 2 回たどる。継続の再開が 2 回になるので <see cref="T:EffectSystem.ContinuationAlreadyResumed"/> が発生する。</summary>
/// <remarks>マニュアルの「ephemeral」に当たる。Freer 版の <c>Advanced.invert</c> では何度たどってもよい。</remarks>
let ephemeralSequence () : unit =
    match invert (fun push -> List.iter push [ 1; 2; 3 ]) () with
    | Cons(_, rest) ->
        rest () |> ignore
        rest () |> ignore
    | Nil -> ()

/// <summary>要素型が同じ <c>invert</c> を入れ子にする。外側の <c>push</c> を内側のハンドラーが受けてしまう例である。</summary>
/// <remarks>
/// マニュアルの <c>invert</c> は呼び出しごとに局所モジュールで新しい操作を作るので、入れ子にしても混ざらない。
/// F# の <see cref="T:EffectSystem.Samples.OCamlManual.Next`1"/> は要素型が同じなら同じ型である。
/// マニュアルの意味論から導くと OCaml では <c>[1; 20]</c> になるはずの計算が、ここでは <c>[10; 20]</c> になる。
/// 能力渡しなら操作を値で指すので混ざらない。
/// </remarks>
let nestedInvertSameType () : int list =
    toList (
        invert (fun outerPush ->
            let inner =
                invert (fun innerPush ->
                    outerPush 1
                    innerPush 2)

            toList inner |> List.iter (fun x -> outerPush (x * 10)))
    )

// ---- 24.5 Semantics ----

/// <summary>整数を返す操作。入れ子の例で、内側のハンドラーが扱う。</summary>
type E =
    /// <summary>整数を要求する。</summary>
    | E

    interface Effect<int>

/// <summary>文字列を返す操作。入れ子の例で、外側のハンドラーが扱う。</summary>
type F =
    /// <summary>文字列を要求する。</summary>
    | F

    interface Effect<string>

/// <summary>操作 F を要求する。</summary>
let foo () : string = perform F

/// <summary>操作 E だけを扱うハンドラーを被せる。F は外へ転送され、外にハンドラーがなければ <see cref="T:EffectSystem.Unhandled"/> になる。</summary>
let bar () : string =
    Deep.tryWith foo [ Deep.ctl (fun (_: E) _ -> failwith "impossible") ]

/// <summary>24.5 操作 F を扱うハンドラーを外側に被せる。F は内側を素通りしてここへ届く。</summary>
let baz () : string =
    Deep.tryWith bar [ Deep.ctl (fun (_: F) k -> Deep.resume k "Hello, world!") ]

/// <summary>24.5 継続を 2 回再開する。<see cref="T:EffectSystem.ContinuationAlreadyResumed"/> が発生する。</summary>
/// <remarks>OCaml の <c>try perform (Xchg 0) with | effect Xchg n, k -> continue k 21 + continue k 21</c> に当たる。</remarks>
let linearContinuations () : int =
    Deep.tryWith
        (fun () -> perform (Xchg 0))
        [ Deep.ctl (fun (Xchg _) k -> Deep.resume k 21 + Deep.resume k 21) ]

// ---- 24.6 Shallow handlers ----

/// <summary>整数を送る操作。</summary>
type Send =
    /// <summary>送る整数を渡す。</summary>
    | Send of int

    interface Effect<unit>

/// <summary>整数を受け取る操作。</summary>
type Recv =
    /// <summary>整数を要求する。</summary>
    | Recv

    interface Effect<int>

/// <summary>次の操作として Send だけを許すハンドラーで再開する。</summary>
/// <remarks>
/// <c>(Send;Recv)*;Send?</c> を強制する。時間とともに変わる「処理する操作の集合」を、相互再帰で表している。
/// OCaml の <c>type a.</c> は、F# ではモジュール直下の明示的な型引数になる。
/// </remarks>
let rec private loopSend<'a> (k: Shallow.Continuation<'a, unit>) (v: 'a) : unit =
    Shallow.continueWith
        k
        v
        { retc = id
          exnc = rethrow
          effc =
            [ Shallow.ctl (fun (Send n) k -> loopRecv<unit> n k ())
              Shallow.ctl (fun (_: Recv) (_: Shallow.Continuation<int, unit>) -> failwith "protocol violation") ] }

/// <summary>次の操作として Recv だけを許すハンドラーで再開する。</summary>
and private loopRecv<'a> (n: int) (k: Shallow.Continuation<'a, unit>) (v: 'a) : unit =
    Shallow.continueWith
        k
        v
        { retc = id
          exnc = rethrow
          effc =
            [ Shallow.ctl (fun (_: Recv) k -> loopSend<int> k n)
              Shallow.ctl (fun (Send _) (_: Shallow.Continuation<unit, unit>) -> failwith "protocol violation") ] }

/// <summary>計算にプロトコルを強制して実行する。違反すると例外になる。</summary>
/// <param name="comp">実行する計算。</param>
let runProtocol (comp: unit -> unit) : unit = loopSend<unit> (Shallow.fiber comp) ()

/// <summary>24.6 プロトコルに従う計算。</summary>
/// <param name="out">出力先。</param>
let shallowFollowsProtocol (out: Log) : unit =
    runProtocol (fun () ->
        out "Send 42"
        perform (Send 42)
        out $"Recv: {perform Recv}"
        out "Send 43"
        perform (Send 43)
        out $"Recv: {perform Recv}")

/// <summary>24.6 Send を 2 回続けて、プロトコルに違反する計算。</summary>
/// <param name="out">出力先。</param>
let shallowViolatesProtocol (out: Log) : unit =
    runProtocol (fun () ->
        out "Send 0"
        perform (Send 0)
        out "Send 1"
        perform (Send 1))

/// <summary>プロトコルと無関係な操作は、外側のハンドラーへ転送される。</summary>
let shallowForwardsOtherEffects () : string =
    Deep.tryWith
        (fun () ->
            let mutable got = ""

            runProtocol (fun () ->
                perform (Send 1)
                got <- perform F)

            got)
        [ Deep.ctl (fun (_: F) k -> Deep.resume k "forwarded") ]

/// <summary>OCaml 5 の <c>Effect.Deep</c> と <c>Effect.Shallow</c> を、普通の関数のまま書ける形で再現するエンジン。</summary>
/// <remarks>
/// <para>
/// <b>継続をスレッドのスタックに置く。</b>
/// 計算を <c>eff { }</c> で書き換えずに <see cref="M:EffectSystem.Direct.perform``1(EffectSystem.Effect{``0})"/> で止めるには、
/// その時点のスタックを丸ごと保存する必要がある。.NET には限定継続もスタックの切り替えもないので、
/// ファイバーごとに専用のスレッドを割り当て、止まったスレッドのスタックをそのまま継続として使う。
/// </para>
/// <para>
/// <b>実行権を手渡しする。</b>
/// ハンドラー側とファイバー側は交互に動き、同時に動くスレッドは常に 1 つである。
/// そのためデータ競合は起きず、見かけ上は 1 本の処理の流れとして読める。
/// </para>
/// <para>
/// <b>再開は 1 回限り。</b>
/// スレッドのスタックは複製できないので、2 回目の再開には応えようがない。
/// OCaml がワンショットである理由と同じ制約が、同じ形で現れる。
/// </para>
/// <para>
/// <b>代償。</b>
/// ファイバーごとにスレッドを 1 本使い、再開されない継続のスレッドはプロセスが終わるまで止まったままになる。
/// 操作はスレッド境界を越えられない。別のスレッドで <c>perform</c> すると <see cref="T:EffectSystem.Unhandled"/> になる。
/// 検証は小規模な例に限っており、本番向けの実装ではない。
/// </para>
/// </remarks>
/// <namespacedoc>
///   <summary>操作の宣言と、スレッドで継続を実現する Direct エンジン。</summary>
///   <remarks>
///   <see cref="T:EffectSystem.Effect`1"/> はすべてのエンジンが共有する操作の印である。
///   Direct エンジンは <c>EffectSystem.Direct</c> モジュールにあり、OCaml のマニュアルの例をそのままの形で動かせる。
///   </remarks>
/// </namespacedoc>
/// <category index="3">エンジン</category>
module EffectSystem.Direct

open System.Runtime.ExceptionServices
open System.Threading
open EffectSystem

/// <summary>ハンドラー側からファイバーへ送る再開の指示。</summary>
/// <remarks>
/// スレッドをまたぐので、回答の型は <c>obj</c> に消してある。
/// 型の整合は <c>Deep.ctl</c> と <c>Deep.resume</c> の型引数が保証する。
/// </remarks>
type internal Resumption =
    /// <summary>値で再開する。<c>perform</c> がこの値を返す。</summary>
    | Value of obj
    /// <summary>例外で再開する。<c>perform</c> の位置でこの例外が発生する。</summary>
    | Raise of exn

/// <summary>ファイバーからハンドラー側へ送る、止まった理由の通知。</summary>
/// <remarks>モデルの <c>return(r)</c> と <c>op(a, k)</c> に、例外での終了を加えた 3 通りになる。</remarks>
type internal Signal =
    /// <summary>計算が値を返して終わった。</summary>
    | Returned of obj
    /// <summary>計算が例外で終わった。</summary>
    | Raised of exn
    /// <summary>計算が操作を要求して止まった。継続はファイバーのスタックそのものなので、ここには含まれない。</summary>
    | Performed of obj

/// <summary>1 通だけ受け渡す窓口。送信と受信は必ず交互に起きる。</summary>
/// <remarks>
/// 実行権の手渡しそのものである。セマフォの <c>Release</c> と <c>Wait</c> が順序を保証するので、
/// <c>message</c> にロックは要らない。計測では操作 1 回の往復が約 400〜600 ナノ秒で、1 コアに固定すると約 1.8 マイクロ秒だった。
/// </remarks>
type internal Handoff<'m>() =
    let ready = new SemaphoreSlim(0)
    let mutable message = Unchecked.defaultof<'m>

    /// <summary>メッセージを置いて相手を起こす。</summary>
    member _.Send(m: 'm) =
        message <- m
        ready.Release() |> ignore

    /// <summary>メッセージが届くまで止まる。</summary>
    member _.Receive() : 'm =
        ready.Wait()
        message

/// <summary>専用スレッドの上で動く計算。止まっている間のスレッドのスタックが継続になる。</summary>
/// <remarks>
/// <para>
/// スレッドは最初の再開のときに起動する。Shallow の <c>fiber</c> のように、作っただけで動かさないためである。
/// </para>
/// <para>
/// 今のスレッドが属するファイバーを <c>ThreadStatic</c> に持つ。
/// <c>perform</c> はこれをたどって親のハンドラーへ通知するので、ハンドラーの中で <c>perform</c> すれば
/// 自然に 1 つ外側のハンドラーへ届く。別のスレッドでは <c>null</c> になるので、操作はスレッド境界を越えられない。
/// </para>
/// </remarks>
[<AllowNullLiteral>]
type internal Fiber(body: obj -> obj) =
    /// <summary>今のスレッドが属するファイバー。スレッドごとに別の値を持つ。</summary>
    [<System.ThreadStatic; DefaultValue>]
    static val mutable private current: Fiber

    let toFiber = Handoff<Resumption>()
    let toHandler = Handoff<Signal>()
    let mutable started = false

    /// <summary>今のスレッドが属するファイバー。ハンドラーの外なら <c>null</c>。</summary>
    static member Current = Fiber.current

    /// <summary>ファイバーのスレッドの本体。最初の再開を待ち、計算を動かし、終わりを通知する。</summary>
    member private this.Main() =
        Fiber.current <- this

        let signal =
            match toFiber.Receive() with
            | Raise e -> Raised e
            | Value v ->
                try
                    Returned(body v)
                with e ->
                    Raised e

        toHandler.Send signal

    /// <summary>ファイバーを動かし、次に中断するか終わるまで呼び出し側を止める。</summary>
    member this.Resume(r: Resumption) : Signal =
        if not started then
            started <- true
            // 再開されないまま捨てられたファイバーがプロセス終了を妨げないようにする。
            Thread(this.Main, IsBackground = true).Start()

        toFiber.Send r
        toHandler.Receive()

    /// <summary>ファイバーの中から呼ばれ、実行権をハンドラー側へ返して、再開されるまで止まる。</summary>
    member _.Suspend(eff: obj) : Resumption =
        toHandler.Send(Performed eff)
        toFiber.Receive()

/// <summary>型を消した形で操作を要求する。転送でも使うので <c>perform</c> から分けている。</summary>
let private performRaw (eff: obj) : obj =
    match Fiber.Current with
    | null -> raise (Unhandled eff)
    | fiber ->
        match fiber.Suspend eff with
        | Value v -> v
        | Raise e -> raise e

/// <summary>操作を要求し、ハンドラーが再開するまで止まって、回答を返す。OCaml の <c>perform</c> に当たる。</summary>
/// <remarks>
/// 普通の関数なので、<c>perform (Xchg 0) + perform (Xchg 1)</c> のように式の途中に書ける。
/// 囲むハンドラーがなければ、この場所で <see cref="T:EffectSystem.Unhandled"/> が発生する。
/// </remarks>
/// <param name="eff">要求する操作。</param>
/// <returns>ハンドラーが渡した回答。</returns>
let perform (eff: Effect<'a>) : 'a = unbox<'a> (performRaw (box eff))

/// <summary>例外を発生元のスタックトレースごと投げ直す。<c>exnc</c> の既定値に使う。OCaml の <c>raise</c> に当たる。</summary>
/// <remarks>ファイバーの例外は別のスレッドで発生するので、<c>raise</c> のままでは発生元の情報が失われる。</remarks>
/// <param name="e">投げ直す例外。</param>
let rethrow (e: exn) : 'r =
    ExceptionDispatchInfo.Capture(e).Throw()
    Unchecked.defaultof<'r>

/// <summary>外側のハンドラーへ転送し、得た結果を子のファイバーへの再開の指示にする。</summary>
/// <remarks>
/// OCaml の <c>effc</c> で <c>None</c> を返したときの動作に当たる。
/// 外側の <c>discontinue</c> も、外側にハンドラーがないときの <see cref="T:EffectSystem.Unhandled"/> も、
/// 例外としてここに届くので、そのまま元の <c>perform</c> の位置へ送る。
/// </remarks>
let private reperform (eff: obj) : Resumption =
    try
        Value(performRaw eff)
    with e ->
        Raise e

/// <summary>Deep ハンドラー。OCaml の <c>Effect.Deep</c> に当たる。</summary>
/// <remarks>継続がハンドラーごと捕捉されるので、再開後の操作も同じハンドラーが受ける。</remarks>
module Deep =

    /// <summary><c>'x</c> を受け取り、ハンドラーの結果 <c>'r</c> を返す 1 回限りの継続。OCaml の <c>continuation</c> に当たる。</summary>
    /// <typeparam name="'x">操作の回答型。</typeparam>
    /// <typeparam name="'r">ハンドラー全体の結果の型。</typeparam>
    /// <remarks>
    /// <para>
    /// 再開の回数を数えるフラグを持つ。2 回目の再開は <see cref="T:EffectSystem.ContinuationAlreadyResumed"/> になる。
    /// 「少なくとも 1 回」は OCaml と同じく検査しない。再開しなければスレッドが止まったまま残る。
    /// </para>
    /// <para>
    /// 実体はファイバーのスレッドにあり、この型は再開の手続きを持つだけである。
    /// コンストラクターを公開していないのは、<c>'x</c> が操作の回答型と一致することをライブラリの内側で保証するためである。
    /// </para>
    /// </remarks>
    [<Sealed>]
    type Continuation<'x, 'r> internal (run: Resumption -> 'r) =
        let mutable resumed = 0

        /// <summary>1 回限りの検査をしてから再開する。</summary>
        member internal _.Run(r: Resumption) : 'r =
            if Interlocked.Exchange(&resumed, 1) = 1 then
                raise ContinuationAlreadyResumed

            run r

    /// <summary>操作を受けた分岐が次に何をするかを表す。</summary>
    /// <remarks><c>Answer</c> と <c>Control</c> を分けたことで、末尾再開だけはスタックを伸ばさずに済む。</remarks>
    type internal Action<'r> =
        /// <summary>この分岐は操作を扱わない。</summary>
        | Forward
        /// <summary>回答が決まった。エンジンのループの中で再開する。<c>Deep.fn</c> が返す。</summary>
        | Answer of Resumption
        /// <summary>継続を分岐へ渡す。<c>Deep.ctl</c> が返す。</summary>
        | Control of ((Resumption -> 'r) -> 'r)

    /// <summary><c>match_with</c> の <c>effc</c> の 1 分岐。<c>Deep.ctl</c> か <c>Deep.fn</c> で作る。</summary>
    /// <typeparam name="'r">ハンドラー全体の結果の型。</typeparam>
    /// <remarks>
    /// OCaml の <c>effc</c> は <c>'b. 'b Effect.t -> ...</c> という多相関数で、1 つの <c>match</c> にすべての分岐を書く。
    /// F# の関数はこの形の多相を引数に取れないので、分岐ごとに型を確定させ、そのリストを <c>effc</c> とした。
    /// </remarks>
    [<NoEquality; NoComparison>]
    type Case<'r> = internal Case of (obj -> Action<'r>)

    /// <summary>ハンドラー。OCaml の <c>('a, 'r) handler</c> レコードをそのまま写している。</summary>
    /// <typeparam name="'a">計算が返す値の型。</typeparam>
    /// <typeparam name="'r">ハンドラー全体の結果の型。</typeparam>
    [<NoEquality; NoComparison>]
    type Handler<'a, 'r> =
        { /// <summary>計算が値を返したときの処理。</summary>
          retc: 'a -> 'r
          /// <summary>計算が例外で終わったときの処理。そのまま投げ直すなら <c>rethrow</c> を指定する。</summary>
          exnc: exn -> 'r
          /// <summary>操作の分岐。先に書いたものから順に試す。どれにも合わない操作は外側へ転送する。</summary>
          effc: Case<'r> list }

    /// <summary>継続を明示的に受け取る分岐を作る。Koka の <c>ctl</c> に当たる。</summary>
    /// <remarks>
    /// <paramref name="f"/> は呼び出し側のスレッドで動く。後で再開するために継続を保存してもよい。
    /// 再開をループへ戻す仕組みがないので、再開ごとに呼び出しが積まれ、スタックの伸びを防ぐには <c>.tail</c> に頼ることになる。
    /// 1 MB のスタックでは <c>Tailcalls=false</c> のとき 5,000 回の操作で溢れた。
    /// </remarks>
    /// <example>
    /// <code lang="fsharp"><![CDATA[
    /// // OCaml: try comp1 () with | effect (Xchg n), k -> continue k (n+1)
    /// Deep.tryWith comp1 [ Deep.ctl (fun (Xchg n) k -> Deep.resume k (n + 1)) ]
    /// ]]></code>
    /// </example>
    /// <typeparam name="'E">この分岐が扱う操作の型。ラムダの引数から決まる。</typeparam>
    /// <typeparam name="'x">操作の回答型。<c>'E</c> の制約から決まる。</typeparam>
    /// <typeparam name="'r">ハンドラー全体の結果の型。</typeparam>
    /// <param name="f">操作と継続を受け取り、ハンドラーの結果を返す関数。</param>
    let ctl<'E, 'x, 'r when 'E :> Effect<'x>> (f: 'E -> Continuation<'x, 'r> -> 'r) : Case<'r> =
        Case(fun eff ->
            match eff with
            | :? 'E as e -> Control(fun run -> f e (Continuation<'x, 'r>(run)))
            | _ -> Forward)

    /// <summary>必ず 1 回だけ末尾で再開する分岐を作る。Koka の <c>fun</c> に当たる。</summary>
    /// <remarks>
    /// エンジンのループの中で答えを返すので、<c>.tail</c> がなくても操作の回数だけスタックが伸びることがない。
    /// <paramref name="f"/> が投げた例外は <c>perform</c> の位置に届く。
    /// </remarks>
    /// <typeparam name="'E">この分岐が扱う操作の型。ラムダの引数から決まる。</typeparam>
    /// <typeparam name="'x">操作の回答型。<c>'E</c> の制約から決まる。</typeparam>
    /// <typeparam name="'r">ハンドラー全体の結果の型。</typeparam>
    /// <param name="f">操作を受け取り、回答を返す関数。</param>
    let fn<'E, 'x, 'r when 'E :> Effect<'x>> (f: 'E -> 'x) : Case<'r> =
        Case(fun eff ->
            match eff with
            | :? 'E as e ->
                Answer(
                    try
                        Value(box (f e))
                    with ex ->
                        Raise ex
                )
            | _ -> Forward)

    /// <summary>操作に合う最初の分岐を探す。</summary>
    let private select (cases: Case<'r> list) (eff: obj) : Action<'r> =
        let rec go =
            function
            | [] -> Forward
            | Case c :: rest ->
                match c eff with
                | Forward -> go rest
                | action -> action

        go cases

    /// <summary>ファイバーからの通知を処理する、ハンドラーの本体。</summary>
    /// <remarks>
    /// 継続は <c>drive</c> をもう一度呼ぶ関数なので、再開後の操作も同じハンドラーが受ける。これが Deep の意味である。
    /// 自分自身への末尾呼び出しはループに変換されるので、<c>Answer</c> と <c>Forward</c> の経路ではスタックが伸びない。
    /// </remarks>
    let rec private drive (fiber: Fiber) (h: Handler<'a, 'r>) (signal: Signal) : 'r =
        match signal with
        | Returned v -> h.retc (unbox<'a> v)
        | Raised e -> h.exnc e
        | Performed eff ->
            match select h.effc eff with
            | Answer r -> drive fiber h (fiber.Resume r)
            | Control run -> run (fun r -> drive fiber h (fiber.Resume r))
            | Forward -> drive fiber h (fiber.Resume(reperform eff))

    /// <summary>継続を値で再開する。OCaml の <c>continue</c> に当たる。</summary>
    /// <remarks>
    /// <c>continue</c> は F# の予約語（FS0046）なので、Koka に倣って <c>resume</c> と呼ぶ。
    /// 呼び出し側のスレッドは、計算が次に中断するか終わるまで止まり、ハンドラーの結果を受け取る。
    /// </remarks>
    /// <param name="k">再開する継続。</param>
    /// <param name="v"><c>perform</c> が返す値。</param>
    let resume (k: Continuation<'x, 'r>) (v: 'x) : 'r = k.Run(Value(box v))

    /// <summary>継続を例外で再開する。OCaml の <c>discontinue</c> に当たる。</summary>
    /// <remarks>
    /// 中断している <c>perform</c> の位置で例外が発生し、計算の中の <c>finally</c> が走る。
    /// 再開できなくなったタスクのリソースを解放する手段になる。
    /// </remarks>
    /// <param name="k">再開する継続。</param>
    /// <param name="e"><c>perform</c> の位置で発生させる例外。</param>
    let discontinue (k: Continuation<'x, 'r>) (e: exn) : 'r = k.Run(Raise e)

    /// <summary>計算にハンドラーを被せて実行する。OCaml の <c>match_with</c> に当たる。</summary>
    /// <param name="f">実行する計算。新しいファイバーの上で動く。</param>
    /// <param name="h">ハンドラー。</param>
    let matchWith (f: unit -> 'a) (h: Handler<'a, 'r>) : 'r =
        let fiber = Fiber(fun _ -> box (f ()))
        drive fiber h (fiber.Resume(Value null))

    /// <summary>結果をそのまま返すハンドラーを被せて実行する。OCaml の <c>try_with</c> に当たる。</summary>
    /// <param name="f">実行する計算。</param>
    /// <param name="cases">操作の分岐。</param>
    let tryWith (f: unit -> 'a) (cases: Case<'a> list) : 'a =
        matchWith f { retc = id; exnc = rethrow; effc = cases }

/// <summary>Shallow ハンドラー。OCaml の <c>Effect.Shallow</c> に当たる。</summary>
/// <remarks>
/// 最初の 1 回の操作だけを扱い、継続にハンドラーは含まれない。再開のたびにハンドラーを渡すので、
/// プロトコルの強制のように、処理する操作の集合が時間とともに変わる場面で自然に書ける。
/// </remarks>
module Shallow =

    /// <summary><c>'x</c> を受け取り、最終的に <c>'b</c> を返すファイバー。OCaml の <c>('x, 'b) Effect.Shallow.continuation</c> に当たる。</summary>
    /// <typeparam name="'x">再開するときに渡す値の型。</typeparam>
    /// <typeparam name="'b">計算が最後に返す値の型。</typeparam>
    /// <remarks>
    /// Deep の継続と違い、結果の型にハンドラーの結果は現れない。ハンドラーは再開のときに決まるからである。
    /// 再開は同じく 1 回限りである。
    /// </remarks>
    [<Sealed>]
    type Continuation<'x, 'b> internal (fiber: Fiber) =
        let mutable resumed = 0

        /// <summary>1 回限りの検査をしてからファイバーを動かす。</summary>
        member internal _.Run(r: Resumption) : Signal =
            if Interlocked.Exchange(&resumed, 1) = 1 then
                raise ContinuationAlreadyResumed

            fiber.Resume r

        /// <summary>この継続の実体であるファイバー。</summary>
        member internal _.Fiber = fiber

    /// <summary>Shallow ハンドラーの 1 分岐。<c>Shallow.ctl</c> で作る。</summary>
    /// <typeparam name="'b">計算が最後に返す値の型。</typeparam>
    /// <typeparam name="'c">このハンドラーの結果の型。</typeparam>
    [<NoEquality; NoComparison>]
    type Case<'b, 'c> = internal Case of (Fiber -> obj -> (unit -> 'c) option)

    /// <summary>Shallow ハンドラー。OCaml の <c>('b, 'c) handler</c> レコードをそのまま写している。</summary>
    /// <typeparam name="'b">計算が最後に返す値の型。</typeparam>
    /// <typeparam name="'c">このハンドラーの結果の型。</typeparam>
    [<NoEquality; NoComparison>]
    type Handler<'b, 'c> =
        { /// <summary>計算が値を返したときの処理。</summary>
          retc: 'b -> 'c
          /// <summary>計算が例外で終わったときの処理。</summary>
          exnc: exn -> 'c
          /// <summary>操作の分岐。どれにも合わない操作は外側へ転送し、同じハンドラーで待ち続ける。</summary>
          effc: Case<'b, 'c> list }

    /// <summary>継続を受け取る分岐を作る。</summary>
    /// <remarks>受け取った継続を再開するときに、次のハンドラーを選ぶ。</remarks>
    /// <typeparam name="'E">この分岐が扱う操作の型。ラムダの引数から決まる。</typeparam>
    /// <typeparam name="'x">操作の回答型。<c>'E</c> の制約から決まる。</typeparam>
    /// <typeparam name="'b">計算が最後に返す値の型。</typeparam>
    /// <typeparam name="'c">このハンドラーの結果の型。</typeparam>
    /// <param name="f">操作と継続を受け取り、結果を返す関数。</param>
    let ctl<'E, 'x, 'b, 'c when 'E :> Effect<'x>> (f: 'E -> Continuation<'x, 'b> -> 'c) : Case<'b, 'c> =
        Case(fun fiber eff ->
            match eff with
            | :? 'E as e -> Some(fun () -> f e (Continuation<'x, 'b>(fiber)))
            | _ -> None)

    /// <summary>ファイバーからの通知を 1 回の操作まで処理する。</summary>
    /// <remarks>Deep の <c>drive</c> と違い、分岐が受け取る継続にこのハンドラーは含まれない。転送した操作だけは数えずに待ち続ける。</remarks>
    let rec private step (fiber: Fiber) (h: Handler<'b, 'c>) (signal: Signal) : 'c =
        match signal with
        | Returned v -> h.retc (unbox<'b> v)
        | Raised e -> h.exnc e
        | Performed eff ->
            match h.effc |> List.tryPick (fun (Case c) -> c fiber eff) with
            | Some run -> run ()
            | None -> step fiber h (fiber.Resume(reperform eff))

    /// <summary>関数をまだ始まっていない継続に変える。OCaml の <c>fiber</c> に当たる。</summary>
    /// <param name="f">計算。最初の再開で渡す値を引数に取る。</param>
    let fiber (f: 'a -> 'b) : Continuation<'a, 'b> =
        Continuation<'a, 'b>(Fiber(fun v -> box (f (unbox<'a> v))))

    /// <summary>ハンドラーを指定して、継続を値で再開する。OCaml の <c>continue_with</c> に当たる。</summary>
    /// <param name="k">再開する継続。</param>
    /// <param name="v">渡す値。</param>
    /// <param name="h">次の操作を受けるハンドラー。</param>
    let continueWith (k: Continuation<'a, 'b>) (v: 'a) (h: Handler<'b, 'c>) : 'c =
        step k.Fiber h (k.Run(Value(box v)))

    /// <summary>ハンドラーを指定して、継続を例外で再開する。OCaml の <c>discontinue_with</c> に当たる。</summary>
    /// <param name="k">再開する継続。</param>
    /// <param name="e">中断している <c>perform</c> の位置で発生させる例外。</param>
    /// <param name="h">次の操作を受けるハンドラー。</param>
    let discontinueWith (k: Continuation<'a, 'b>) (e: exn) (h: Handler<'b, 'c>) : 'c =
        step k.Fiber h (k.Run(Raise e))

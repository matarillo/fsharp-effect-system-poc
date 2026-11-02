namespace EffectSystem.Freer

open EffectSystem

/// <summary>計算を「次の操作と、その後の継続」として表すデータ。モデルの <c>Comp(E, R)</c> に当たる。</summary>
/// <typeparam name="'a">計算が最後に返す値の型。</typeparam>
/// <remarks>
/// <para>
/// <b>継続をヒープに置く。</b>
/// 継続はスタックではなく普通のクロージャなので、何度でも呼べる。呼ばなくてもよい。
/// Koka や Unison のマルチショットと、Extensible Effects の「計算はデータ」という性質は、
/// どちらもこの表現から得られる。組み立てただけでは何も実行されず、同じ値を何度でも実行できる。
/// </para>
/// <para>
/// <b>エフェクトの集合を型に持たない。</b>
/// F# に行多相はないので、Koka の <c>&lt;ask&lt;a&gt; | e&gt;</c> に当たるものをこの型には持たせていない。
/// そのためハンドラーの付け忘れは <c>Eff.run</c> まで分からない。静的な検査が要るときは、
/// この型の上に能力渡し（<see cref="N:EffectSystem.Capability"/>）を載せる。
/// </para>
/// <para>
/// <b>Functor で抽象化しない。</b>
/// Haskell の <c>Free f</c> は任意の Functor を受け取るが、F# は型コンストラクターを型引数に渡せない。
/// 木の型をこの 1 つに固定し、節には操作を <c>obj</c> として入れた。
/// これでエフェクトの和も Functor の定義も要らなくなる。
/// </para>
/// </remarks>
/// <category index="1">計算</category>
[<NoEquality; NoComparison>]
type Eff<'a> =
    /// <summary>値を返して終わった計算。モデルの <c>return(r)</c> に当たる。</summary>
    | Pure of 'a
    /// <summary>操作を要求して止まっている計算。モデルの <c>op(a, k)</c> に当たる。</summary>
    /// <remarks>
    /// GADT がないので、操作と回答は <c>obj</c> に消してある。
    /// 回答の型が操作と食い違わないことは、節を作る <c>Eff.perform</c> と、節を受ける <c>Eff.ctl</c> の型制約が保証する。
    /// 継続が <c>Result</c> を受け取るのは、値だけでなく例外でも再開するためである。
    /// これにより OCaml の <c>discontinue</c> を表現でき、計算の中の <c>try</c> にも例外が届く。
    /// </remarks>
    | Impure of effect: obj * resume: (Result<obj, exn> -> Eff<'a>)
    /// <summary>まだ実行していない計算。</summary>
    /// <remarks>
    /// 役割は 2 つある。1 つは遅延で、<c>eff { }</c> の最初の区間を実行時まで動かさない。
    /// もう 1 つはトランポリンの足場で、ハンドラーの再開がこの節を返して <c>Eff.run</c> のループへ戻ることで、
    /// <c>.tail</c> 呼び出しに頼らずスタックの伸びを防ぐ。
    /// </remarks>
    | Delayed of (unit -> Eff<'a>)
    /// <summary>つなげた計算。ZIO の <c>FlatMap</c> に当たる。</summary>
    /// <remarks>
    /// <c>Eff.bind</c> はこの節を作るだけで、前の計算を進めない。進めるのは <c>Eff.run</c> とハンドラーのループである。
    /// 再帰しないのでスタックは伸びない。左にネストした分は進めるときに右へ付け替えるので、操作あたりのコストも深さに比例しない。
    /// </remarks>
    | Bound of BindNode<'a>

/// <summary><c>Bound</c> の中身。前の計算の結果の型を隠す。</summary>
/// <typeparam name="'a">つなげた計算全体の結果の型。</typeparam>
/// <remarks>
/// F# に存在型がないので、前の計算の型を抽象クラスの内側へ閉じ込める。
/// 再開を何度でもできるように、節は不変である。ZIO の実行ループのように可変の配列へ積むことはしない。
/// </remarks>
/// <category index="1">計算</category>
and [<AbstractClass>] BindNode<'a> internal () =
    /// <summary>計算を 1 段だけ進める。</summary>
    abstract Step: unit -> Eff<'a>
    /// <summary>後ろに次の計算をつなげる。左にネストした <c>bind</c> を右へ付け替える。</summary>
    abstract Then<'b> : ('a -> Eff<'b>) -> Eff<'b>

/// <summary>前の計算と、その結果を受け取る次の計算の組。<c>Eff.bind</c> が作る。</summary>
/// <typeparam name="'x">前の計算の結果の型。</typeparam>
/// <typeparam name="'a">つなげた計算全体の結果の型。</typeparam>
/// <category index="1">計算</category>
and [<Sealed>] internal BindOf<'x, 'a>(m: Eff<'x>, f: 'x -> Eff<'a>) =
    inherit BindNode<'a>()

    override _.Step() =
        match m with
        | Pure x -> f x
        | Delayed th -> Bound(BindOf<'x, 'a>(th (), f))
        | Impure(e, k) ->
            Impure(
                e,
                (fun r ->
                    match k r with
                    | Pure x -> f x
                    | n -> Bound(BindOf<'x, 'a>(n, f)))
            )
        | Bound inner -> inner.Then f

    override _.Then<'b>(g: 'a -> Eff<'b>) : Eff<'b> =
        // f x をその場で呼ぶと、同じ前の計算に足し重ねた節の分だけ再帰する。
        Bound(BindOf<'x, 'b>(m, (fun x -> Bound(BindOf<'a, 'b>(Delayed(fun () -> f x), g)))))

/// <summary>回答型が <c>'x</c> の操作の継続。値でも例外でも再開できる。</summary>
/// <typeparam name="'x">操作の回答型。</typeparam>
/// <typeparam name="'r">ハンドラー全体の結果の型。</typeparam>
/// <remarks>
/// <para>
/// 多くのハンドラーは値で再開するだけなので、<c>Eff.ctl</c> は継続を素の関数として渡す。
/// 例外での再開が要るときだけ、<c>Eff.ctlK</c> でこの型を受け取る。
/// </para>
/// <para>
/// コンストラクターを公開していないのは、<c>'x</c> が操作の回答型と一致することをライブラリの内側で保証するためである。
/// 再開の回数は数えていない。Direct エンジンの <see cref="T:EffectSystem.Direct.Deep.Continuation`2"/> と違い、何度呼んでもよい。
/// </para>
/// </remarks>
/// <category index="2">ハンドラー</category>
[<Sealed>]
type Continuation<'x, 'r> internal (k: Result<obj, exn> -> Eff<'r>) =
    /// <summary>値で再開する。OCaml の <c>continue</c>、Koka の <c>resume</c> に当たる。</summary>
    member _.Resume(x: 'x) : Eff<'r> = k (Ok(box x))

    /// <summary>例外で再開する。止まっていた <c>Eff.perform</c> の位置で例外が発生する。OCaml の <c>discontinue</c> に当たる。</summary>
    member _.Discontinue(e: exn) : Eff<'r> = k (Error e)

/// <summary>ハンドラーの分岐の中身。操作を扱うかどうかの判定と、扱うときの実行を分けたもの。</summary>
/// <typeparam name="'r">ハンドラー全体の結果の型。</typeparam>
/// <remarks>
/// <para>
/// 操作のたびに呼ばれるので、判定を仮想メソッドにして、操作ごとの <c>option</c> とクロージャの割り当てを避けている。
/// </para>
/// <para>
/// 答えをその場で返す分岐は継続を作らずに済むので、<c>Capture</c> とは別に <c>Answer</c> を持つ。
/// どちらを呼ぶかは <c>AnswersDirectly</c> で決める。
/// </para>
/// </remarks>
/// <category index="2">ハンドラー</category>
[<AbstractClass; AllowNullLiteral>]
type internal CaseImpl<'r>(answersDirectly: bool) =
    /// <summary>継続を作らず、<c>Answer</c> で答えを返す分岐かどうか。</summary>
    member _.AnswersDirectly = answersDirectly

    /// <summary>この分岐が操作を扱うかどうかを返す。</summary>
    abstract Handles: effect: obj -> bool

    /// <summary>答えをその場で返す。例外は <c>Error</c> にして、<c>Eff.perform</c> の位置へ届ける。</summary>
    abstract Answer: effect: obj -> Result<obj, exn>

    /// <summary>継続を受け取って分岐を実行する。</summary>
    abstract Capture: effect: obj * resume: (Result<obj, exn> -> Eff<'r>) -> Eff<'r>

/// <summary>答えをその場で返す分岐。<c>Eff.fn</c> が作る。</summary>
/// <category index="2">ハンドラー</category>
[<Sealed>]
type internal FnCase<'E, 'x, 'r when 'E :> Effect<'x>>(f: 'E -> 'x) =
    inherit CaseImpl<'r>(true)
    override _.Handles effect = effect :? 'E

    override _.Answer effect =
        try
            Ok(box (f (unbox<'E> effect)))
        with e ->
            Error e

    override this.Capture(effect, resume) = resume (this.Answer effect)

/// <summary>継続を受け取る分岐。<c>Eff.ctl</c> と <c>Eff.ctlK</c> が作る。</summary>
/// <category index="2">ハンドラー</category>
[<Sealed>]
type internal CtlCase<'E, 'r>(f: 'E -> (Result<obj, exn> -> Eff<'r>) -> Eff<'r>) =
    inherit CaseImpl<'r>(false)
    override _.Handles effect = effect :? 'E
    override _.Answer _ = invalidOp "継続を受け取る分岐はその場で答えを返せない"
    override _.Capture(effect, resume) = f (unbox<'E> effect) resume

/// <summary>ハンドラーの 1 分岐。<c>Eff.ctl</c>、<c>Eff.fn</c>、<c>Eff.interpret</c> で作る。</summary>
/// <typeparam name="'r">ハンドラー全体の結果の型。</typeparam>
/// <remarks>
/// <para>
/// OCaml の <c>effc</c> は <c>'b. 'b Effect.t -> ...</c> という多相関数で、1 つの <c>match</c> にすべての分岐を書く。
/// F# の関数はこの形の多相を引数に取れないので、分岐ごとに型を確定させ、そのリストをハンドラーとした。
/// </para>
/// <para>
/// 型引数に操作の型 <c>'E</c> と回答型 <c>'x</c> が現れないのは、種類の違う分岐を同じリストに入れるためである。
/// 内部表現を隠しているので、利用者の書いた分岐が型の整合を壊すことはない。
/// </para>
/// </remarks>
/// <category index="2">ハンドラー</category>
[<NoEquality; NoComparison>]
type Case<'r> = internal Case of CaseImpl<'r>

/// <summary>次の操作まで進めて覗いた計算の形。Unison の <c>Request {e, Ask a} r</c> に当たる。</summary>
/// <typeparam name="'a">計算が最後に返す値の型。</typeparam>
/// <remarks>
/// <see cref="T:EffectSystem.Freer.Eff`1"/> から実装の都合である <c>Delayed</c> と <c>Bound</c> を取り除き、モデルどおりの 2 つの形だけを見せる。
/// 再帰を手で書くハンドラーは継続にハンドラーを自動で含めないので、実質的に Shallow ハンドラーになる。
/// 状態を引数で持ち回るハンドラーを書くときに向いている。
/// </remarks>
/// <category index="2">ハンドラー</category>
[<NoEquality; NoComparison>]
type Request<'a> =
    /// <summary>計算は値を返して終わった。Unison の <c>{ a }</c> に当たる。</summary>
    | Return of 'a
    /// <summary>計算は操作で止まっている。Unison の <c>{ Ask.ask -> k }</c> に当たる。通常は <c>Eff.Op</c> パターンで受ける。</summary>
    | Operation of effect: obj * resume: (Result<obj, exn> -> Eff<'a>)

/// <summary><c>Eff.perform</c> の継続。回答を <c>obj</c> から戻す。</summary>
/// <typeparam name="'a">操作の回答型。</typeparam>
/// <remarks>どの操作でも同じ関数なので、操作ごとに作らず、型ごとに 1 個だけ作って共有する。</remarks>
/// <category index="1">計算</category>
[<Sealed>]
type internal PerformResume<'a> private () =
    /// <summary>回答を受け取って <c>Pure</c> にする関数。例外で再開されたらその場で投げる。</summary>
    static member val Instance: Result<obj, exn> -> Eff<'a> =
        (function
        | Ok x -> Pure(unbox<'a> x)
        | Error e -> raise e)

/// <summary>計算の組み立て、ハンドラーの構築、実行を行う関数群。</summary>
/// <namespacedoc>
///   <summary>Freer エンジン。継続をヒープ上のクロージャとして持ち、Koka / Unison / Polysemy の意味論を再現する。</summary>
///   <remarks>
///   計算は <see cref="T:EffectSystem.Freer.Eff`1"/> というデータで、ハンドラーはその木を畳み込む関数である。
///   継続が普通の関数なので何度でも再開でき、組み立てと実行が分かれる。
///   計算は <c>eff { }</c> で書き、式の途中で操作を呼ぶことはできない。
///   </remarks>
/// </namespacedoc>
/// <category index="1">計算</category>
[<RequireQualifiedAccess>]
module Eff =

    /// <summary>値を返すだけの計算を作る。<c>return</c> に当たる。</summary>
    /// <remarks><c>pure</c> は F# の予約語（FS0046）なので、この名前にしている。</remarks>
    let ret (x: 'a) : Eff<'a> = Pure x

    /// <summary>実行時まで遅延した計算を作る。</summary>
    let delay (f: unit -> Eff<'a>) : Eff<'a> = Delayed f

    /// <summary>計算をつなげる。モデルの <c>>>=</c> に当たる。</summary>
    /// <remarks>
    /// モデルの等式をそのまま再帰で書くと、左にネストした <c>bind</c> を再開のたびにたどるので、操作あたりのコストがネストの深さに比例する。
    /// これは Freer の知られた弱点で、型整列キューで解く方法もあるが、ここでは <c>Bound</c> の節を作るだけにして、進めるときに右へ付け替える。
    /// </remarks>
    /// <param name="f">前の計算の結果を受け取る、次の計算。</param>
    /// <param name="m">前の計算。</param>
    let bind (f: 'a -> Eff<'b>) (m: Eff<'a>) : Eff<'b> =
        match m with
        | Pure x -> f x
        // 作りたての perform は節を作らずに包む。いちばん多い形で、再帰もしない。
        | Impure(e, k) when obj.ReferenceEquals(k, PerformResume<'a>.Instance) ->
            Impure(
                e,
                (fun r ->
                    match k r with
                    | Pure x -> f x
                    | n -> Bound(BindOf<'a, 'b>(n, f)))
            )
        | _ -> Bound(BindOf<'a, 'b>(m, f))

    /// <summary>計算の結果に関数を適用する。</summary>
    let map (f: 'a -> 'b) (m: Eff<'a>) : Eff<'b> = bind (f >> Pure) m

    /// <summary>操作を要求する計算を作る。</summary>
    /// <remarks>
    /// Direct エンジンの <c>perform</c> と違い、ここでは何も起きず、操作のデータを返すだけである。
    /// 回答型は <paramref name="eff"/> の型で決まっており、ハンドラーの分岐も同じ型で作られるので、
    /// 継続の中で回答を <c>obj</c> から戻す <c>unbox</c> は失敗しない。
    /// </remarks>
    /// <param name="eff">要求する操作。</param>
    let perform (eff: Effect<'a>) : Eff<'a> =
        Impure(box eff, PerformResume<'a>.Instance)

    /// <summary>すべての操作が処理された計算を実行する。モデルの <c>run</c> に当たる。</summary>
    /// <remarks>
    /// <c>Delayed</c> と <c>Bound</c> を受けるたびにこのループへ戻るので、スタックは伸びない。
    /// 操作が残っていれば <see cref="T:EffectSystem.Unhandled"/> になる。型で検査できないので、ここが最後の防壁である。
    /// </remarks>
    /// <param name="m">実行する計算。</param>
    let run (m: Eff<'a>) : 'a =
        let rec loop m =
            match m with
            | Pure x -> x
            | Delayed th -> loop (th ())
            | Bound n -> loop (n.Step())
            | Impure(e, _) -> raise (Unhandled e)

        loop m

    /// <summary>計算の全区間に <c>try ... with</c> を掛ける。</summary>
    /// <remarks>
    /// F# の <c>try</c> は最初の区間しか覆えないので、再開のたびに掛け直す。
    /// 入れ子の <c>try</c> は操作のたびに全段を通るので、コストとスタックが入れ子の深さに比例する。
    /// </remarks>
    let rec private tryWith (m: unit -> Eff<'a>) (h: exn -> Eff<'a>) : Eff<'a> =
        let step =
            try
                Choice1Of2(m ())
            with e ->
                Choice2Of2 e

        match step with
        | Choice2Of2 e -> h e
        | Choice1Of2(Pure x) -> Pure x
        | Choice1Of2(Delayed th) -> Delayed(fun () -> tryWith th h)
        // 節はこの場で進める。Delayed で返すと、入れ子の try を 1 歩ごとに通り直す。
        | Choice1Of2(Bound n) -> tryWith n.Step h
        | Choice1Of2(Impure(e, k)) -> Impure(e, (fun r -> tryWith (fun () -> k r) h))

    /// <summary>計算の全区間に <c>try ... finally</c> を掛ける。</summary>
    /// <remarks>
    /// マルチショットでは経路ごとに終端があるので、<c>fin</c> は経路の数だけ走る。
    /// 再開されずに捨てられた経路では走らない。確実に走らせるには <c>Continuation.Discontinue</c> で再開する。
    /// 入れ子のコストは <c>tryWith</c> と同じである。
    /// </remarks>
    let rec private tryFinally (m: unit -> Eff<'a>) (fin: unit -> unit) : Eff<'a> =
        let step =
            try
                m ()
            with _ ->
                fin ()
                reraise ()

        match step with
        | Pure x ->
            fin ()
            Pure x
        | Delayed th -> Delayed(fun () -> tryFinally th fin)
        // 節はこの場で進める。理由は tryWith と同じである。
        | Bound n -> tryFinally n.Step fin
        | Impure(e, k) -> Impure(e, (fun r -> tryFinally (fun () -> k r) fin))

    /// <summary>継続を <see cref="T:EffectSystem.Freer.Continuation`2"/> として受け取る分岐を作る。</summary>
    /// <remarks>例外で再開したいときに使う。それ以外は <c>Eff.ctl</c> で足りる。</remarks>
    /// <typeparam name="'E">この分岐が扱う操作の型。ラムダの引数の注釈から決まる。</typeparam>
    /// <typeparam name="'x">操作の回答型。<c>'E</c> の制約から決まる。</typeparam>
    /// <typeparam name="'r">ハンドラー全体の結果の型。</typeparam>
    /// <param name="f">操作と継続を受け取り、ハンドラーの結果を返す関数。</param>
    let ctlK<'E, 'x, 'r when 'E :> Effect<'x>> (f: 'E -> Continuation<'x, 'r> -> Eff<'r>) : Case<'r> =
        Case(CtlCase<'E, 'r>(fun e k -> f e (Continuation<'x, 'r>(k))))

    /// <summary>継続を受け取る分岐を作る。Koka の <c>ctl</c> に当たる。継続は何度呼んでも、呼ばなくてもよい。</summary>
    /// <remarks>
    /// 継続の呼び方でハンドラーの意味が決まる。呼ばなければ例外、末尾で 1 回なら依存性の注入、
    /// 複数回なら非決定計算になる。
    /// 呼ばずに捨てた経路では、計算の中の <c>finally</c> が走らない。走らせるには <c>Eff.ctlK</c> の <c>Discontinue</c> で再開する。
    /// </remarks>
    /// <example>
    /// <code lang="fsharp"><![CDATA[
    /// // Koka: with ctl ask() resume(value)
    /// action |> Eff.handle [ Eff.ctl (fun (_: Ask<string>) resume -> resume "value") ]
    /// ]]></code>
    /// </example>
    /// <typeparam name="'E">この分岐が扱う操作の型。ラムダの引数の注釈から決まる。</typeparam>
    /// <typeparam name="'x">操作の回答型。<c>'E</c> の制約から決まる。</typeparam>
    /// <typeparam name="'r">ハンドラー全体の結果の型。</typeparam>
    /// <param name="f">操作と継続の関数を受け取り、ハンドラーの結果を返す関数。</param>
    let ctl<'E, 'x, 'r when 'E :> Effect<'x>> (f: 'E -> ('x -> Eff<'r>) -> Eff<'r>) : Case<'r> =
        Case(CtlCase<'E, 'r>(fun e k -> f e (fun x -> k (Ok(box x)))))

    /// <summary>必ず 1 回だけ末尾で再開する分岐を作る。Koka の <c>fun</c> に当たる。</summary>
    /// <remarks>
    /// 継続を受け取らないので、再開の回数を間違えようがない。
    /// <paramref name="f"/> が投げた例外は <c>Eff.perform</c> の位置に届く。
    /// 継続のクロージャを作らずに答えを受け渡すので、<c>Eff.ctl</c> より速い。Koka が <c>fun</c> をその場で実行するのと同じ考え方である。
    /// </remarks>
    /// <typeparam name="'E">この分岐が扱う操作の型。ラムダの引数の注釈から決まる。</typeparam>
    /// <typeparam name="'x">操作の回答型。<c>'E</c> の制約から決まる。</typeparam>
    /// <typeparam name="'r">ハンドラー全体の結果の型。</typeparam>
    /// <param name="f">操作を受け取り、回答を返す関数。</param>
    let fn<'E, 'x, 'r when 'E :> Effect<'x>> (f: 'E -> 'x) : Case<'r> = Case(FnCase<'E, 'x, 'r>(f))

    /// <summary>操作を別の計算へ置き換える分岐を作る。Polysemy の <c>interpret</c> に当たる。</summary>
    /// <remarks>
    /// 継続は見えず、置き換えた計算の結果で自動的に 1 回だけ再開する。
    /// 「Extensible Effects の継続は暗黙で 1 回だけ」という説明はこの API の性質で、方式の制約ではない。
    /// 同じ <c>Eff</c> の上で <c>Eff.ctl</c> を使えば、継続は何度でも呼べる。
    /// </remarks>
    /// <typeparam name="'E">この分岐が扱う操作の型。ラムダの引数の注釈から決まる。</typeparam>
    /// <typeparam name="'x">操作の回答型。<c>'E</c> の制約から決まる。</typeparam>
    /// <typeparam name="'r">ハンドラー全体の結果の型。</typeparam>
    /// <param name="f">操作を受け取り、置き換える計算を返す関数。</param>
    let interpret<'E, 'x, 'r when 'E :> Effect<'x>> (f: 'E -> Eff<'x>) : Case<'r> =
        ctlK<'E, 'x, 'r> (fun e k -> bind k.Resume (f e))

    /// <summary>操作を扱う分岐を、先に書いたものから順に探す。なければ <c>null</c> を返す。</summary>
    let private findCase (cases: CaseImpl<'r>[]) (effect: obj) : CaseImpl<'r> =
        let mutable i = 0

        while i < cases.Length && not (cases[i].Handles effect) do
            i <- i + 1

        if i < cases.Length then cases[i] else null

    /// <summary>Deep ハンドラーの本体。モデルの <c>handle H</c> の定義そのものである。</summary>
    /// <remarks>
    /// 継続を <c>deep</c> で包むので、再開後の操作も同じハンドラーが受ける。これが Deep の意味である。
    /// 分岐に渡す再開を <c>Delayed</c> で包むと <c>Eff.run</c> のループへ戻るので、末尾再開ならスタックは伸びない。
    /// 答えをその場で返す分岐と <c>Delayed</c>、<c>Bound</c> の節は、自己末尾呼び出しなのでコンパイラがループにする。
    /// </remarks>
    let rec private deep (retc: 'a -> Eff<'r>) (cases: CaseImpl<'r>[]) (m: Eff<'a>) : Eff<'r> =
        match m with
        | Pure x -> retc x
        | Delayed th -> deep retc cases (th ())
        | Bound n -> deep retc cases (n.Step())
        | Impure(e, k) ->
            match findCase cases e with
            | null -> Impure(e, (fun r -> Delayed(fun () -> deep retc cases (k r))))
            | case when case.AnswersDirectly -> deep retc cases (k (case.Answer e))
            | case -> case.Capture(e, (fun r -> Delayed(fun () -> deep retc cases (k r))))

    /// <summary>結果の変換を指定して、計算に Deep ハンドラーを被せる。</summary>
    /// <param name="retc">計算が値を返したときの変換。Koka の <c>return</c> 節、Unison の <c>{ a } -> ...</c> に当たる。</param>
    /// <param name="cases">ハンドラーの分岐。先に書いたものから順に試す。どれにも合わない操作は外側へ転送する。</param>
    /// <param name="m">ハンドラーを被せる計算。</param>
    let handleWith (retc: 'a -> Eff<'r>) (cases: Case<'r> list) (m: Eff<'a>) : Eff<'r> =
        let impls = [| for Case impl in cases -> impl |]
        Delayed(fun () -> deep retc impls m)

    /// <summary>計算に Deep ハンドラーを被せる。結果はそのまま返す。</summary>
    /// <param name="cases">ハンドラーの分岐。先に書いたものから順に試す。どれにも合わない操作は外側へ転送する。</param>
    /// <param name="m">ハンドラーを被せる計算。</param>
    let handle (cases: Case<'a> list) (m: Eff<'a>) : Eff<'a> = handleWith Pure cases m

    /// <summary>次の操作まで進めて覗く。Unison 流に手で再帰するハンドラーや、状態を持つハンドラーに使う。</summary>
    /// <param name="m">覗く計算。</param>
    let rec view (m: Eff<'a>) : Request<'a> =
        match m with
        | Pure x -> Return x
        | Delayed th -> view (th ())
        | Bound n -> view (n.Step())
        | Impure(e, k) -> Operation(e, k)

    /// <summary><c>view</c> で覗いた結果のうち、自分が扱わない操作を外側へ転送する。</summary>
    /// <param name="req">覗いた結果。</param>
    /// <param name="rehandle">再開後の計算に、もう一度ハンドラーを被せる関数。</param>
    let forward (req: Request<'a>) (rehandle: Eff<'a> -> Eff<'b>) : Eff<'b> =
        match req with
        | Return x -> rehandle (Pure x)
        | Operation(e, k) -> Impure(e, (fun r -> Delayed(fun () -> rehandle (k r))))

    /// <summary>覗いた計算が操作 <c>'E</c> で止まっているときに、操作と継続を取り出すパターン。Unison の <c>{ Ask.ask -> k }</c> に当たる。</summary>
    /// <remarks><c>'E</c> はパターンの注釈で決まり、<c>'x</c> は <c>'E</c> の制約から決まる。</remarks>
    /// <param name="req">覗いた結果。</param>
    let (|Op|_|) (req: Request<'a>) : ('E * ('x -> Eff<'a>)) option when 'E :> Effect<'x> =
        match req with
        | Operation((:? 'E as e), k) -> Some(e, (fun x -> k (Ok(box x))))
        | _ -> None

    /// <summary><c>eff { }</c> のビルダー。</summary>
    /// <remarks>
    /// <c>Delay</c> が <c>Delayed</c> を返すので、計算の本体は実行時まで動かない。
    /// <c>Run</c> は定義していない。<c>eff { }</c> の値は組み立てただけの計算で、何度でも実行できる。
    /// </remarks>
    type Builder() =
        /// <summary><c>return x</c></summary>
        member _.Return(x: 'a) : Eff<'a> = Pure x

        /// <summary><c>return! m</c></summary>
        member _.ReturnFrom(m: Eff<'a>) : Eff<'a> = m

        /// <summary>末尾にある <c>return! m</c> と <c>do! m</c>。F# 10 のコンパイラが選ぶ（FS-1330）。</summary>
        /// <remarks>
        /// これがないと末尾の <c>do! m</c> は <c>Bind</c> になり、再帰すると <c>Bound</c> の節が深さの分だけ残る。
        /// <c>m</c> をそのまま返せば節を作らないので、<c>do!</c> の再帰はメモリも増えない。
        /// <c>try</c> の中はコンパイラが末尾として扱わないので、例外の捕捉は変わらない。
        /// </remarks>
        member _.ReturnFromFinal(m: Eff<'a>) : Eff<'a> = m

        /// <summary><c>let! x = m</c></summary>
        member _.Bind(m: Eff<'a>, f: 'a -> Eff<'b>) : Eff<'b> = bind f m

        /// <summary>値を返さない本体。</summary>
        member _.Zero() : Eff<unit> = Pure()

        /// <summary>本体を遅延する。</summary>
        member _.Delay(f: unit -> Eff<'a>) : Eff<'a> = Delayed f

        /// <summary>文を順に実行する。</summary>
        member _.Combine(a: Eff<unit>, b: Eff<'a>) : Eff<'a> = bind (fun () -> b) a

        /// <summary><c>try ... with</c>。再開後の区間と、例外での再開も捕捉する。</summary>
        member _.TryWith(m: Eff<'a>, h: exn -> Eff<'a>) : Eff<'a> = tryWith (fun () -> m) h

        /// <summary><c>try ... finally</c>。経路ごとに走り、捨てられた経路では走らない。</summary>
        member _.TryFinally(m: Eff<'a>, fin: unit -> unit) : Eff<'a> = tryFinally (fun () -> m) fin

        /// <summary><c>use x = r</c>。解放の条件は <c>try ... finally</c> と同じである。</summary>
        member _.Using(r: 'r when 'r :> System.IDisposable, f: 'r -> Eff<'a>) : Eff<'a> =
            tryFinally (fun () -> f r) (fun () -> r.Dispose())

        /// <summary><c>while</c>。各回を <c>Delayed</c> で区切るので、回数が増えてもスタックは伸びない。</summary>
        member b.While(cond: unit -> bool, body: Eff<unit>) : Eff<unit> =
            Delayed(fun () ->
                if cond () then
                    bind (fun () -> b.While(cond, body)) body
                else
                    Pure())

        /// <summary><c>for x in xs</c>。</summary>
        /// <remarks>
        /// 列挙子は可変で再開の間で共有されてしまうので、先に不変リストへ変換してマルチショットに耐えさせている。
        /// そのため無限列は扱えない。
        /// </remarks>
        member _.For(xs: seq<'x>, f: 'x -> Eff<unit>) : Eff<unit> =
            let rec go =
                function
                | [] -> Pure()
                | x :: rest -> bind (fun () -> go rest) (Delayed(fun () -> f x))

            Delayed(fun () -> go (List.ofSeq xs))

/// <summary><c>eff { }</c> を提供する。名前空間を開くだけで使えるように自動で開く。</summary>
/// <category index="1">計算</category>
[<AutoOpen>]
module Builders =
    /// <summary>計算を書くコンピュテーション式。Haskell の do 記法に当たる。</summary>
    /// <example>
    /// <code lang="fsharp"><![CDATA[
    /// let program: Eff<string> =
    ///     eff {
    ///         let! (name: string) = Eff.perform Ask
    ///         return "Hello, " + name
    ///     }
    /// ]]></code>
    /// </example>
    let eff = Eff.Builder()

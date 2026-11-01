namespace EffectSystem.Capability

open EffectSystem
open EffectSystem.Freer

/// <summary>ハンドラーの位置を表す目印。<c>reset</c> の実行ごとに別物になる。</summary>
/// <typeparam name="'r">この区切りの計算全体の結果の型。</typeparam>
/// <remarks>
/// <para>
/// 型で操作を探す方式では、同じ型のハンドラーが重なると常に一番内側が選ばれる。
/// プロンプトは値なので、型が同じでも区切りを指定できる。これが Effekt の字句スコープに当たる。
/// </para>
/// <para>
/// 型引数 <c>'r</c> があるので、<c>shift</c> で取り出す継続の結果の型が、区切りの結果の型と必ず一致する。
/// 利用者が作る必要はないので、コンストラクターは公開していない。
/// </para>
/// </remarks>
/// <category index="2">限定継続</category>
[<Sealed>]
type Prompt<'r> internal () =
    class
    end

/// <summary><c>Control</c> の型引数 <c>'x</c> を隠して、ハンドラーが区切りの結果の型だけで扱えるようにする。</summary>
/// <category index="2">限定継続</category>
type internal IControl<'r> =
    /// <summary>取り出す継続の区切り。</summary>
    abstract Prompt: Prompt<'r>
    /// <summary>継続を受け取って本体を実行する。</summary>
    abstract Invoke: (Result<obj, exn> -> Eff<'r>) -> Eff<'r>

/// <summary><c>shift</c> が発行する、ただ 1 種類の操作。</summary>
/// <remarks>
/// 能力渡しでは操作の種類を型で区別しない。利用者の操作はインターフェイスのメソッドで、
/// その実装がこの操作を発行する。Freer の木から見れば、すべての能力の呼び出しがこの 1 種類になる。
/// </remarks>
/// <category index="2">限定継続</category>
type internal Control<'x, 'r>(prompt: Prompt<'r>, body: ('x -> Eff<'r>) -> Eff<'r>) =
    interface Effect<'x>

    interface IControl<'r> with
        member _.Prompt = prompt
        member _.Invoke k = body (fun x -> k (Ok(box x)))

/// <summary><c>reset</c> が被せるハンドラーの分岐。型ではなくプロンプトで操作を探す。</summary>
/// <remarks>同じ型のハンドラーが重なっていても、自分のプロンプトを指す <c>Control</c> だけを受ける。</remarks>
/// <category index="2">限定継続</category>
[<Sealed>]
type internal PromptCase<'r>(prompt: Prompt<'r>) =
    inherit CaseImpl<'r>(false)

    override _.Handles effect =
        match effect with
        | :? IControl<'r> as c -> obj.ReferenceEquals(c.Prompt, prompt)
        | _ -> false

    override _.Answer _ = invalidOp "継続を受け取る分岐はその場で答えを返せない"
    override _.Capture(effect, resume) = (effect :?> IControl<'r>).Invoke resume

/// <summary>限定継続の基本操作。能力の実装に使う。</summary>
/// <namespacedoc>
///   <summary>Effekt の意味論（字句スコープのハンドラー、能力渡し）を Freer の上に再現する。</summary>
///   <remarks>
///   <para>
///   操作を型で探す代わりに、ハンドラーが作った能力（インターフェイスの値）を計算へ直接渡す。
///   能力を持っていなければ操作を呼べないので、ハンドラーの付け忘れはコンパイルエラーになる。
///   行とは「必要なものの一覧」なので、その値を渡すことにすれば、スコープが行と同じ役割を果たす。
///   </para>
///   <para>
///   Effekt と違い、能力をハンドラーの外へ持ち出すことは型で禁じられない。持ち出した能力を使うと実行時に
///   <see cref="T:EffectSystem.Unhandled"/> になる。
///   </para>
///   </remarks>
/// </namespacedoc>
/// <category index="2">限定継続</category>
[<AutoOpen>]
module Delimited =

    /// <summary>限定継続の区切りを置く。Effekt の <c>try { ... } with ...</c> の <c>try</c> に当たる。</summary>
    /// <remarks>
    /// Freer の <c>Eff.handle</c> に「型の代わりにプロンプトで探す」分岐を 1 つ渡すだけで実現している。
    /// プロンプトは実行のたびに作るので、同じ計算を何度実行しても区切りが混ざらない。
    /// </remarks>
    /// <param name="body">プロンプトを受け取る計算。能力を作って本体へ渡す。</param>
    let reset (body: Prompt<'r> -> Eff<'r>) : Eff<'r> =
        Eff.delay (fun () ->
            let prompt = Prompt<'r>()
            Eff.handle [ Case(PromptCase<'r>(prompt)) ] (body prompt))

    /// <summary>指定した区切りまでの継続を取り出す。能力の実装の中で使い、<c>resume</c> を受け取る。</summary>
    /// <remarks>
    /// <para>継続はヒープ上の関数なので、Freer と同じく何度でも呼べる。</para>
    /// <para>
    /// <b>末尾で 1 回再開するだけなら要らない。</b>
    /// 能力のメソッドは <c>Eff&lt;'a&gt;</c> を返すだけなので、<c>Eff.ret a</c> で答えれば継続を取り出さずに済み、
    /// <c>reset</c> も要らなくなる。割り当てが減って約 5 倍速くなる。
    /// 代わりに、ハンドラーの外へ持ち出した能力が <see cref="T:EffectSystem.Unhandled"/> にならず、そのまま答えを返す。
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="fsharp"><![CDATA[
    /// // Effekt: try { action() } with Ask[A] { def ask() = resume(a) }
    /// let askHandler (a: 'a) (action: IAsk<'a> -> Eff<'r>) : Eff<'r> =
    ///     reset (fun prompt ->
    ///         action
    ///             { new IAsk<'a> with
    ///                 member _.Ask() = shift prompt (fun resume -> resume a) })
    /// ]]></code>
    /// </example>
    /// <param name="prompt">継続の区切り。</param>
    /// <param name="body">継続を受け取り、区切りの結果を返す関数。</param>
    let shift (prompt: Prompt<'r>) (body: ('x -> Eff<'r>) -> Eff<'r>) : Eff<'x> =
        Eff.perform (Control<'x, 'r>(prompt, body))

/// <summary>必要な能力の集合を型引数 <c>'R</c> で表す計算。インターフェイス制約がエフェクト行の代わりになる。</summary>
/// <typeparam name="'R">環境の型。計算が必要とする能力のインターフェイスを実装する。</typeparam>
/// <typeparam name="'a">計算が最後に返す値の型。</typeparam>
/// <remarks>
/// <para>
/// 能力の引数を省くための型である。実体は環境を受け取る関数で、Reader と同じ形をしている。
/// 環境の型が <c>'R</c> という 1 つの型変数なので、計算をつなげると F# の推論が制約を集める。
/// 注釈がなくても次の型が得られ、Koka の <c>&lt;ask&lt;a&gt;, emit&lt;a&gt;&gt; ()</c> に最も近い姿になる。
/// </para>
/// <code lang="fsharp"><![CDATA[
/// val askEmitRow: out: Log -> Rio<'a,unit> when 'a :> IEmit<'b> and 'a :> IAsk<'b>
/// ]]></code>
/// <para>
/// <b>制限。</b>
/// 行を 1 段ずつ消すことはできず、最後に環境をまとめて具体クラスとして与える。
/// 環境に必要なインターフェイスが欠けていれば <c>Rio.run</c> の呼び出しが FS0193 になる。
/// 同じジェネリックインターフェイスの別のインスタンスを同時に要求すると、推論が 1 つに単一化して FS0001 になるので、
/// <c>IAskAge</c> と <c>IAskName</c> のように名前を分ける。
/// </para>
/// </remarks>
/// <category index="1">能力と環境</category>
[<Struct; NoEquality; NoComparison>]
type Rio<'R, 'a> =
    /// <summary>環境を受け取って計算を返す関数。</summary>
    | Rio of ('R -> Eff<'a>)

/// <summary><see cref="T:EffectSystem.Capability.Rio`2"/> の関数群。</summary>
/// <category index="1">能力と環境</category>
[<RequireQualifiedAccess>]
module Rio =

    /// <summary>環境を与えて、Freer の計算に戻す。ここで行が閉じる。</summary>
    let run (env: 'R) (Rio f: Rio<'R, 'a>) : Eff<'a> = f env

    /// <summary>環境を使わない計算を持ち上げる。</summary>
    let lift (m: Eff<'a>) : Rio<'R, 'a> = Rio(fun _ -> m)

    /// <summary>計算をつなげる。両方が同じ <c>'R</c> を共有するので、制約が 1 つに集まる。</summary>
    let bind (f: 'a -> Rio<'R, 'b>) (m: Rio<'R, 'a>) : Rio<'R, 'b> =
        Rio(fun env -> run env m |> Eff.bind (fun x -> run env (f x)))

    /// <summary><c>rio { }</c> のビルダー。</summary>
    type Builder() =
        /// <summary><c>return x</c></summary>
        member _.Return(x: 'a) : Rio<'R, 'a> = lift (Eff.ret x)

        /// <summary><c>return! m</c></summary>
        member _.ReturnFrom(m: Rio<'R, 'a>) : Rio<'R, 'a> = m

        /// <summary>末尾にある <c>return! m</c> と <c>do! m</c>。理由は <c>eff { }</c> のビルダーと同じである。</summary>
        member _.ReturnFromFinal(m: Rio<'R, 'a>) : Rio<'R, 'a> = m

        /// <summary><c>let! x = m</c></summary>
        member _.Bind(m: Rio<'R, 'a>, f: 'a -> Rio<'R, 'b>) : Rio<'R, 'b> = bind f m

        /// <summary>値を返さない本体。</summary>
        member _.Zero() : Rio<'R, unit> = lift (Eff.ret ())

        /// <summary>本体を遅延する。環境が与えられ、計算が実行されるまで動かない。</summary>
        member _.Delay(f: unit -> Rio<'R, 'a>) : Rio<'R, 'a> =
            Rio(fun env -> Eff.delay (fun () -> run env (f ())))

        /// <summary>文を順に実行する。</summary>
        member _.Combine(a: Rio<'R, unit>, b: Rio<'R, 'a>) : Rio<'R, 'a> = bind (fun () -> b) a

/// <summary><c>handlers { }</c> のビルダー。Koka と Effekt の <c>with</c> 糖衣を再現する。</summary>
/// <remarks>
/// <c>with h</c> は <c>h (fun cap -> 残り)</c> の糖衣で、ネストを平らにするだけのものである。
/// F# の <c>let!</c> も同じ変形をするので、<c>Bind</c> を関数適用にすれば同じように書ける。
/// </remarks>
/// <category index="3">糖衣</category>
type HandlersBuilder() =
    /// <summary><c>let! cap = handler</c>。残りの計算を本体として <paramref name="handler"/> に渡す。</summary>
    /// <param name="handler">能力を作って本体へ渡すハンドラー。</param>
    /// <param name="body">能力を受け取る、残りの計算。</param>
    member _.Bind(handler: ('cap -> Eff<'r>) -> Eff<'r>, body: 'cap -> Eff<'r>) : Eff<'r> = handler body

    /// <summary><c>return! m</c>。最も内側の計算を書く。</summary>
    member _.ReturnFrom(m: Eff<'r>) : Eff<'r> = m

/// <summary><c>rio { }</c> と <c>handlers { }</c> を提供する。名前空間を開くだけで使えるように自動で開く。</summary>
/// <category index="1">能力と環境</category>
[<AutoOpen>]
module Builders =
    /// <summary><see cref="T:EffectSystem.Capability.Rio`2"/> を書くコンピュテーション式。</summary>
    let rio = Rio.Builder()

    /// <summary>ハンドラーを平らに重ねるコンピュテーション式。</summary>
    /// <example>
    /// <code lang="fsharp"><![CDATA[
    /// // with askHandler("Ask Value")
    /// // with emitHandler
    /// // askEmit[String]()
    /// handlers {
    ///     let! ask = askHandler "Ask Value"
    ///     let! emit = emitHandler out
    ///     return! askEmit ask emit out
    /// }
    /// |> Eff.run
    /// ]]></code>
    /// </example>
    let handlers = HandlersBuilder()

namespace EffectSystem

/// <summary>回答型が <c>'a</c> である操作の印。OCaml の <c>'a Effect.t</c> に当たる。</summary>
/// <typeparam name="'a">操作の回答型。ハンドラーが継続へ渡す値の型になる。</typeparam>
/// <remarks>
/// <para>
/// モデルの上では、操作はシグネチャ <c>op : A ⇝ B</c> に過ぎず、実装を持たない。
/// この型はその <c>B</c> だけを型に刻む印で、メンバーを持たない。
/// 操作の宣言はこの印にしか依存しないので、同じ宣言を <see cref="T:EffectSystem.Freer.Eff`1"/> でも
/// Direct エンジンでも処理できる。
/// </para>
/// <para>
/// <b>なぜインターフェイスなのか。</b>
/// OCaml の <c>type _ Effect.t += ...</c> のように、後から操作を足せる型が F# にはない。
/// 判別共用体は閉じているが、インターフェイスなら誰でも後から実装できる。
/// クラスを継承する形にしなかったのは、判別共用体がクラスを継承できないためである。
/// 操作を判別共用体で書ければ、ハンドラー側でそのままパターンマッチできる。
/// </para>
/// <para>
/// <b>なぜ回答型を型引数に持つのか。</b>
/// OCaml は GADT によって <c>match</c> の中で回答型を詰めるが、F# に GADT はない。
/// 代わりにハンドラーの分岐を <c>'E :> Effect&lt;'x&gt;</c> という制約で受ける。
/// 操作の型 <c>'E</c> が決まれば回答型 <c>'x</c> が決まり、継続の型も決まる。
/// </para>
/// <para>
/// <b>代償。</b>
/// .NET のジェネリクスは実行時に残るので、<c>Ask&lt;string&gt;</c> と <c>Ask&lt;int&gt;</c> は別の操作として区別できる。
/// OCaml で functor が必要な場面が 1 行で済む一方、型が同じ操作はすべて同じ操作として扱われる。
/// OCaml の局所モジュールのように、呼び出しごとに新しい操作を作ることはできない。
/// 操作の網羅性も検査されない。
/// </para>
/// </remarks>
/// <example>
/// 操作は判別共用体で宣言し、このインターフェイスで回答型を示す。
/// <code lang="fsharp"><![CDATA[
/// type Ask<'v> =
///     | Ask
///
///     interface Effect<'v>
///
/// type Emit<'v> =
///     | Emit of 'v
///
///     interface Effect<unit>
/// ]]></code>
/// </example>
/// <category index="1">操作の宣言</category>
type Effect<'a> =
    interface
    end

/// <summary>どのハンドラーにも捕捉されなかった操作を知らせる例外。OCaml の <c>Effect.Unhandled</c> に当たる。</summary>
/// <remarks>
/// 型で操作を探す方式には、ハンドラーの付け忘れを静的に検出する手段がないので、実行時の例外になる。
/// Direct エンジンでは <c>perform</c> の位置で、Freer エンジンでは <c>Eff.run</c> の時点で発生する。
/// 能力渡しでは、ハンドラーの外へ持ち出した能力を使ったときに発生する。
/// 捕捉されなかった操作は <c>effect</c> で取り出せる。
/// </remarks>
/// <category index="2">例外</category>
exception Unhandled of
    /// <summary>捕捉されなかった操作。</summary>
    effect: obj

/// <summary>継続を 2 回再開したことを知らせる例外。OCaml の <c>Effect.Continuation_already_resumed</c> に当たる。</summary>
/// <remarks>
/// Direct エンジンの継続だけが発生させる。止まったスレッドのスタックは複製できないので、
/// 2 回目の再開には応えようがない。ヒープに継続を置く Freer エンジンには、この制約も例外もない。
/// </remarks>
/// <category index="2">例外</category>
exception ContinuationAlreadyResumed

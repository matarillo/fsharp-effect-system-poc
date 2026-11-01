/// <summary>記事の題材である操作の宣言。Direct と Freer のどちらのエンジンからも同じ型を使う。</summary>
/// <remarks>
/// 宣言はシグネチャでしかないので、実行方式に依存しない。
/// 計算の書き方はエンジンごとに変わるが、ここにある宣言は変わらない。
/// </remarks>
/// <namespacedoc>
///   <summary>記事の 6 言語の例と、OCaml マニュアルの例を F# に写したサンプル集。</summary>
///   <remarks>
///   言語ごとにモジュールを分け、元のコードをすぐ上の XML コメントに載せている。
///   実行結果は記事とマニュアルに掲載された出力をテストの期待値にしている。
///   </remarks>
/// </namespacedoc>
/// <category index="1">共通</category>
module EffectSystem.Samples.Effects

open EffectSystem

/// <summary>値を取り出す操作。Koka の <c>effect ask&lt;a&gt; { ctl ask() : a }</c> に当たる。</summary>
/// <typeparam name="'v">取り出す値の型。そのまま操作の回答型になる。</typeparam>
/// <remarks>
/// <c>'v</c> は判別共用体の中身に現れない幽霊型で、<see cref="T:EffectSystem.Effect`1"/> を通じて回答型だけを示す。
/// .NET のジェネリクスは実行時に残るので、<c>Ask&lt;string&gt;</c> と <c>Ask&lt;int&gt;</c> は別の操作になる。
/// OCaml ではこの区別に functor が必要になる。
/// </remarks>
type Ask<'v> =
    /// <summary>値を要求する。</summary>
    | Ask

    interface Effect<'v>

/// <summary>値を発する操作。Koka の <c>effect emit&lt;a&gt; { ctl emit(v : a) : () }</c> に当たる。</summary>
/// <typeparam name="'v">発する値の型。回答型は <c>unit</c> である。</typeparam>
type Emit<'v> =
    /// <summary>値を発する。</summary>
    | Emit of 'v

    interface Effect<unit>

/// <summary>状態を読む操作。</summary>
/// <typeparam name="'s">状態の型。</typeparam>
type Get<'s> =
    /// <summary>今の状態を要求する。</summary>
    | Get

    interface Effect<'s>

/// <summary>状態を書き換える操作。</summary>
/// <typeparam name="'s">状態の型。</typeparam>
type Put<'s> =
    /// <summary>新しい状態を渡す。</summary>
    | Put of 's

    interface Effect<unit>

/// <summary>標準出力の代わりになる関数。テストで出力を検証できるように差し替え可能にしておく。</summary>
/// <remarks>Koka の <c>console</c> のようなエフェクトにはしていない。F# では通常の副作用を型で追跡できないからである。</remarks>
type Log = string -> unit

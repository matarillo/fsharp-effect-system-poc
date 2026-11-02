/// <summary>レコードを環境にして、Rio の転送クラスをなくす手法。</summary>
/// <remarks>
/// <para>
/// <see cref="T:EffectSystem.Capability.Rio`2"/> の環境は、必要なインターフェイスをすべて実装するクラスになる。
/// F# のクラスは条件付きでインターフェイスを実装できないので、能力の数だけ転送を書くことになる。
/// SRTP のメンバー制約で「このメンバーを持つ任意の型」を受ければ、レコードをそのまま環境にできる。
/// </para>
/// <para>
/// <b>なぜ動くのか。</b>
/// 匿名レコードと名前付きレコードのフィールドは、どちらもプロパティとしてメンバー制約を満たす。
/// 環境の型は <c>rio { }</c> のビルダーを通じて 1 つの型変数を共有するので、制約は Rio と同じように集まる。
/// 制約はコンパイル時に解決されるので、実行コストは Rio と変わらない。
/// </para>
/// <para>
/// <b>行は消せない。</b>
/// 能力を足して残りの環境を渡す関数は書けない。<c>{| env with Ask = ask |}</c> は <c>env</c> の型がレコードと分かっていなければ FS3245 になる。
/// 行を閉じる手間は減るが、行を 1 段ずつ消す手段にはならない。
/// </para>
/// <para>
/// <b>代償。</b>
/// 環境を型変数のまま扱う関数はすべて <c>inline</c> にする必要があり、呼び出しのたびにコードが展開される。
/// <c>let rec inline</c> は FS3890 になり、抽象メンバーの型にも書けない（FS0670）。
/// 操作だけを <c>inline</c> にして、組み上げた計算は <see cref="T:EffectSystem.Samples.RecordEnv.AppEnv"/> のような名前付きレコードで閉じると、
/// どちらの問題も避けられる。匿名レコードの型はアセンブリごとに別物になるので、シグネチャに書くのにも名前付きレコードが向いている。
/// </para>
/// </remarks>
/// <category index="4">発展</category>
module EffectSystem.Samples.RecordEnv

open EffectSystem.Freer
open EffectSystem.Capability
open EffectSystem.Samples.Effects
open EffectSystem.Samples.EffektStyle

/// <summary>環境から値を取り出す計算。環境の型に、<c>Ask</c> という名前のメンバーを要求する。</summary>
/// <typeparam name="^R">環境の型。</typeparam>
/// <typeparam name="'v">取り出す値の型。</typeparam>
let inline ask () : Rio< ^R, 'v> when ^R: (member Ask: IAsk<'v>) =
    Rio(fun env -> (^R: (member Ask: IAsk<'v>) env).Ask())

/// <summary>環境を使って値を発する計算。環境の型に、<c>Emit</c> という名前のメンバーを要求する。</summary>
/// <typeparam name="^R">環境の型。</typeparam>
/// <typeparam name="'v">発する値の型。</typeparam>
/// <param name="v">発する値。</param>
let inline emit (v: 'v) : Rio< ^R, unit> when ^R: (member Emit: IEmit<'v>) =
    Rio(fun env -> (^R: (member Emit: IEmit<'v>) env).Emit v)

/// <summary>環境を型変数のまま扱う計算。必要なメンバーの一覧が型として推論される。</summary>
/// <remarks>
/// 注釈なしで次の型に推論される。読みやすさは Rio と同じである。
/// <code lang="fsharp"><![CDATA[
/// val inline askEmit<^a,'b when ^a: (member Ask: IAsk<'b>) and ^a: (member Emit: IEmit<'b>)> :
///   out: Log -> Rio<^a,unit>
/// ]]></code>
/// </remarks>
/// <param name="out">出力先。</param>
let inline askEmit (out: Log) =
    rio {
        out "Start"
        let! v = ask ()
        do! emit v
        out "End"
    }

/// <summary>匿名レコードを環境にして実行する。クラスも転送も要らない。</summary>
/// <param name="out">出力先。</param>
let exampleAskEmit (out: Log) : unit =
    handlers {
        let! ask = askHandler "Ask Value"
        let! emit = emitHandler out
        return! askEmit out |> Rio.run {| Ask = ask; Emit = emit |}
    }
    |> Eff.run

// ---- 組み上げた計算は名前付きレコードで閉じる ----

/// <summary>行を閉じる名前付きレコード。<see cref="T:EffectSystem.Samples.EffektStyle.AppEnv"/> と違い、転送を書かずに済む。</summary>
type AppEnv =
    { /// <summary>値を取り出す能力。</summary>
      Ask: IAsk<string>
      /// <summary>値を発する能力。</summary>
      Emit: IEmit<string> }

/// <summary>環境を閉じた計算。普通の関数なので、再帰が書けて、呼び出しのたびにコードが増えることもない。</summary>
/// <param name="remaining">残りの回数。</param>
let rec repeatAskEmit (remaining: int) : Rio<AppEnv, unit> =
    rio {
        if remaining > 0 then
            let! (v: string) = ask ()
            do! emit v
            do! repeatAskEmit (remaining - 1)
    }

/// <summary>名前付きレコードで行を閉じて実行する。</summary>
/// <param name="out">出力先。</param>
let exampleClosedEnv (out: Log) : unit =
    handlers {
        let! ask = askHandler "Ask Value"
        let! emit = emitHandler out
        return! repeatAskEmit 2 |> Rio.run { Ask = ask; Emit = emit }
    }
    |> Eff.run

// ---- メンバーの名前をラベルとして使う ----

/// <summary>年齢を取り出す計算。環境の型に、<c>Age</c> という名前のメンバーを要求する。</summary>
/// <typeparam name="^R">環境の型。</typeparam>
let inline askAge () : Rio< ^R, int> when ^R: (member Age: IAsk<int>) =
    Rio(fun env -> (^R: (member Age: IAsk<int>) env).Ask())

/// <summary>名前を取り出す計算。環境の型に、<c>Name</c> という名前のメンバーを要求する。</summary>
/// <typeparam name="^R">環境の型。</typeparam>
let inline askName () : Rio< ^R, string> when ^R: (member Name: IAsk<string>) =
    Rio(fun env -> (^R: (member Name: IAsk<string>) env).Ask())

/// <summary>同じジェネリックインターフェイスの別のインスタンスを同時に要求する計算。</summary>
/// <remarks>
/// インターフェイス制約では <c>IAsk&lt;int&gt;</c> と <c>IAsk&lt;string&gt;</c> が 1 つに単一化されて FS0001 になる。
/// メンバーの名前が違えば別の制約になるので、インターフェイスを分けなくても同時に要求できる。
/// 同じ名前のまま別の型を要求すると、同じ理由で FS0001 になる。
/// </remarks>
let inline askBoth () =
    rio {
        let! age = askAge ()
        let! name = askName ()
        return (age, name)
    }

/// <summary>ラベルの違う能力を環境にまとめて実行する。</summary>
/// <returns><c>(30, "Alice")</c></returns>
let exampleTwoLabels () : int * string =
    handlers {
        let! age = askHandler 30
        let! name = askHandler "Alice"
        return! askBoth () |> Rio.run {| Age = age; Name = name |}
    }
    |> Eff.run

// ---- 能力の引数を 1 つにまとめる ----

/// <summary>環境から <c>Ask</c> を取り出すパターン。SRTP の制約を引数のパターンに隠す。</summary>
/// <param name="env">環境。</param>
let inline (|HasAsk|) (env: ^R) = (^R: (member Ask: IAsk<'a>) env)

/// <summary>環境から <c>Emit</c> を取り出すパターン。</summary>
/// <param name="env">環境。</param>
let inline (|HasEmit|) (env: ^R) = (^R: (member Emit: IEmit<'a>) env)

/// <summary>能力の引数を 1 つの環境にまとめた計算。Rio を使わずに書ける。</summary>
/// <remarks>
/// Effekt の <c>def askEmit[A](): Unit / { Ask[A], Emit[A] }</c> に当たる。必要な能力が引数のパターンに現れる。
/// </remarks>
/// <param name="out">出力先。</param>
let inline askEmitCapability (HasAsk ask & HasEmit emit) (out: Log) =
    eff {
        out "Start"
        let! v = ask.Ask()
        do! emit.Emit v
        out "End"
    }

/// <summary>能力を匿名レコードにまとめて渡す。</summary>
/// <param name="out">出力先。</param>
let exampleActivePattern (out: Log) : unit =
    handlers {
        let! ask = askHandler "Ask Value"
        let! emit = emitHandler out
        return! askEmitCapability {| Ask = ask; Emit = emit |} out
    }
    |> Eff.run

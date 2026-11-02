/// <summary>Orsak で、記事の例、途中で 1 回の例、失敗を合わせる例を書く。</summary>
/// <remarks>
/// <para>
/// Orsak の <c>Effect&lt;'r, 'a, 'e&gt;</c> は、環境 <c>'r</c> を受け取って <c>ValueTask&lt;Result&lt;'a, 'e&gt;&gt;</c> を始める関数である。
/// 環境の扱いは <c>Rio</c> と同じインターフェイス制約で、失敗の扱いは <c>ZioStyle</c> の <c>Zio</c> と同じ <c>Result</c> である。
/// 違うのは土台で、継続は .NET の非同期のステートマシンが持つ。再開は 1 回で、利用者には渡らない。
/// </para>
/// <para>
/// ハンドラーはない。操作の意味は環境が渡すオブジェクトが決めるので、能力で <c>Eff.ret</c> を返す形
/// （<c>EffektStyle.askHandlerDirect</c>）に当たる。
/// </para>
/// <para>
/// Orsak もこのプロジェクトも <c>eff</c> を自動で開くので、両方を開くと後から開いた方が前を隠す。このファイルは Freer を開かない。
/// </para>
/// </remarks>
/// <namespacedoc>
///   <summary>F# の先行例 Orsak と FIO で、記事と ZIO の検証の例を書いたサンプル集。</summary>
///   <remarks>
///   ライブラリそのものを使う。比較の結果は docs/orsak-fio-report.md にある。
///   本体とサンプルの依存を増やさないように、別のプロジェクトにしている。
///   </remarks>
/// </namespacedoc>
/// <category index="1">Orsak</category>
module EffectSystem.PriorArt.OrsakStyle

open System.Threading.Tasks
open Orsak
open Orsak.Scoped
open EffectSystem.Samples.Effects

/// <summary>環境を与えて計算を始め、結果を同期に待つ。</summary>
/// <param name="env">環境。</param>
/// <param name="effect">動かす計算。</param>
let run (env: 'r) (effect: Effect<'r, 'a, 'e>) : Result<'a, 'e> =
    (Effect.run env effect).AsTask().GetAwaiter().GetResult()

// ---- 記事の例 ----

/// <summary>値を取り出すサービス。Effekt の <c>interface Ask[A] { def ask(): A }</c> に当たる。</summary>
/// <typeparam name="'a">取り出す値の型。</typeparam>
/// <remarks>メソッドは普通の値を返す。<c>Task</c> を返してもよく、<c>Effect.Create</c> がどちらも受ける。</remarks>
type IAsk<'a> =
    /// <summary>値を要求する。</summary>
    abstract Ask: unit -> 'a

/// <summary>値を発するサービス。Effekt の <c>interface Emit[A] { def emit(value: A): Unit }</c> に当たる。</summary>
/// <typeparam name="'a">発する値の型。</typeparam>
type IEmit<'a> =
    /// <summary>値を発する。</summary>
    abstract Emit: 'a -> unit

/// <summary>環境が <c>IAsk</c> を提供することを示すインターフェイス。Orsak の提供者インターフェイス（provider interface）である。</summary>
/// <typeparam name="'a">取り出す値の型。</typeparam>
/// <remarks>
/// サービスを直接環境の制約にせず、提供者を挟むのが Orsak の慣習である。
/// 提供者のメンバー名がラベルの役割を果たすので、<c>RecordEnv</c> のフィールド名と同じく、型が同じサービスも名前を分ければ同時に要求できる。
/// ジェネリックな提供者を 1 つの環境に 2 通り要求すると、<c>Rio</c> と同じ理由で型が通らない（<c>orsak-two-instantiations.fsx</c>）。
/// </remarks>
type IAskProvider<'a> =
    /// <summary>サービス。</summary>
    abstract Ask: IAsk<'a>

/// <summary>環境が <c>IEmit</c> を提供することを示すインターフェイス。</summary>
/// <typeparam name="'a">発する値の型。</typeparam>
type IEmitProvider<'a> =
    /// <summary>サービス。</summary>
    abstract Emit: IEmit<'a>

/// <summary>環境から値を取り出す計算。環境の型に <c>IAskProvider</c> の制約を加える。<c>Rio</c> の <c>ask</c> に当たる。</summary>
let ask () =
    Effect.Create(fun (p: #IAskProvider<'v>) -> p.Ask.Ask())

/// <summary>環境を使って値を発する計算。環境の型に <c>IEmitProvider</c> の制約を加える。</summary>
/// <param name="v">発する値。</param>
let emit (v: 'v) =
    Effect.Create(fun (p: #IEmitProvider<'v>) -> p.Emit.Emit v)

/// <summary>記事の askEmit。必要なサービスの一覧が型として推論される。</summary>
/// <remarks>
/// 注釈なしで次の型に推論される。<c>EffektStyle.askEmitRow</c> の <c>Rio</c> と同じ形で、失敗の型 <c>'c</c> は自由なまま残る。
/// <code lang="fsharp"><![CDATA[
/// val askEmit: out: Log -> Effect<'a,unit,'c> when 'a :> IEmitProvider<'b> and 'a :> IAskProvider<'b>
/// ]]></code>
/// </remarks>
/// <param name="out">出力先。</param>
let askEmit (out: Log) =
    eff {
        out "Start"
        let! v = ask ()
        do! emit v
        out "End"
    }

/// <summary>行を閉じる具体型。計算が必要とする提供者をすべて実装する。</summary>
/// <remarks>欠けていれば <c>Effect.run</c> の呼び出しが FS0193 になる（<c>orsak-missing-provider.fsx</c>）。</remarks>
/// <param name="out">出力先。</param>
type AppEnv(out: Log) =
    let ask =
        { new IAsk<string> with
            member _.Ask() = "Ask Value" }

    let emit =
        { new IEmit<string> with
            member _.Emit v = out ("Emit " + v) }

    interface IAskProvider<string> with
        member _.Ask = ask

    interface IEmitProvider<string> with
        member _.Emit = emit

/// <summary>環境を与えて askEmit を実行する。</summary>
/// <param name="out">出力先。</param>
let exampleAskEmit (out: Log) : unit =
    match askEmit out |> run (AppEnv out) with
    | Ok() -> ()
    | Error(e: string) -> failwith e

// ---- 途中で 1 回 ----

/// <summary>トランザクション。完了すると commit し、完了せずに破棄されると rollback する。</summary>
/// <remarks>Orsak の <c>CompletableScope</c> は、完了と破棄を別のメンバーに分けて持つ。</remarks>
/// <param name="out">出力先。</param>
type Transaction(out: Log) =
    let mutable completed = false

    /// <summary>トランザクションの中で作業を記録する。</summary>
    /// <param name="message">作業の内容。</param>
    member _.Record(message: string) = out message

    interface CompletableScope with
        member _.Complete() =
            completed <- true
            out "commit"
            ValueTask()

        member _.DisposeAsync() =
            if not completed then
                out "rollback"

            ValueTask()

/// <summary>環境がトランザクションを提供することを示すインターフェイス。</summary>
type ITransactionProvider =
    /// <summary>今のトランザクション。</summary>
    abstract Transaction: Transaction

/// <summary>トランザクションの中で作業を記録する計算。環境の型に <c>ITransactionProvider</c> の制約を加える。</summary>
/// <param name="message">作業の内容。</param>
let record (message: string) =
    Effect.Create(fun (p: #ITransactionProvider) -> p.Transaction.Record message)

/// <summary>トランザクションを持たない外側の環境。<c>IGrowsInto</c> で、トランザクションを提供する環境へ育つ。</summary>
/// <param name="out">出力先。</param>
type BankEnv(out: Log) =
    interface IGrowsInto<Transaction, TransactionEnv> with
        member _.BeginScope() =
            out "begin"
            ValueTask<Transaction>(Transaction out)

        member _.Grow transaction = TransactionEnv transaction

/// <summary>トランザクションの中の環境。</summary>
/// <param name="transaction">今のトランザクション。</param>
and TransactionEnv(transaction: Transaction) =
    interface ITransactionProvider with
        member _.Transaction = transaction

/// <summary>計算をトランザクションの中で動かす。<c>Advanced.transaction</c> に当たる。</summary>
/// <remarks>
/// <para>
/// 継続を受け取らず、スコープの始まりと終わりで前後処理をする。成功したら完了し、失敗でも例外でも破棄する。
/// 継続が利用者に渡らないので、Freer のように捨てられた経路で後始末が飛ぶことはない。
/// </para>
/// <para>
/// 型からトランザクションが消え、外側の環境には <c>IGrowsInto</c> だけが残る。
/// <code lang="fsharp"><![CDATA[
/// val transaction: body: Effect<'a,'b,'c> -> Effect<#IGrowsInto<Transaction,'a>,'b,'c>
/// ]]></code>
/// </para>
/// </remarks>
/// <param name="body">トランザクションの中の計算。</param>
let transaction (body: Effect<'Inner, 'a, 'e>) =
    CompletingScope<Transaction>.Within body

/// <summary>作業が成功するトランザクション。</summary>
/// <param name="out">出力先。</param>
let transactionCommits (out: Log) : unit =
    match eff { do! record "work" } |> transaction |> run (BankEnv out) with
    | Ok() -> ()
    | Error(e: string) -> out $"failed: {e}"

/// <summary>作業が失敗するトランザクション。失敗は値として戻るので、破棄の前に失敗が決まる。</summary>
/// <param name="out">出力先。</param>
let transactionFails (out: Log) : unit =
    let work =
        eff {
            do! record "work"
            return! Error "work failed"
        }

    match work |> transaction |> run (BankEnv out) with
    | Ok() -> ()
    | Error e -> out $"failed: {e}"

// ---- 失敗を合わせる ----

/// <summary>入力の誤りの一覧。<c>+</c> で合わせられるので、<c>and!</c> の失敗の型に使える。</summary>
type Problems =
    /// <summary>誤りの説明の一覧。</summary>
    | Problems of string list

    /// <summary>2 つの一覧をつなげる。</summary>
    static member (+)(Problems a, Problems b) = Problems(a @ b)

/// <summary>商品の指定を確かめる。</summary>
/// <param name="sku">商品。空なら失敗する。</param>
let checkSku (sku: string) =
    eff {
        if sku = "" then
            return! Error(Problems [ "sku is empty" ])
        else
            return sku
    }

/// <summary>数量を確かめる。</summary>
/// <param name="quantity">数量。正でなければ失敗する。</param>
let checkQuantity (quantity: int) =
    eff {
        if quantity <= 0 then
            return! Error(Problems [ "quantity must be positive" ])
        else
            return quantity
    }

/// <summary>注文の行を確かめる。<c>and!</c> は両方を始めてから待ち、両方が失敗したら失敗の型の <c>+</c> で合わせる。</summary>
/// <remarks>
/// 失敗の型が <c>+</c> を持たなければ FS0001 になる（<c>orsak-and-without-plus.fsx</c>）。
/// 同じ形を <c>ZioStyle.checkLine</c> に写している。
/// </remarks>
/// <param name="sku">商品。</param>
/// <param name="quantity">数量。</param>
let checkLine (sku: string) (quantity: int) =
    eff {
        let! sku = checkSku sku
        and! quantity = checkQuantity quantity
        return sku, quantity
    }

/// <summary>誤りを 2 つ含む行を確かめ、両方の誤りを出力する。</summary>
/// <param name="out">出力先。</param>
let exampleCheckLine (out: Log) : unit =
    match checkLine "" 0 |> run () with
    | Ok(sku, quantity) -> out $"{sku} x {quantity}"
    | Error(Problems problems) -> problems |> List.iter out

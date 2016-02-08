﻿#r "System.Windows.Forms.DataVisualization.dll"
#I "./packages/"
#r "Rx-Interfaces/lib/net45/System.Reactive.Interfaces.dll"
#r "Rx-Core/lib/net45/System.Reactive.Core.dll"
#r "Rx-Linq/lib/net45/System.Reactive.Linq.dll"
#r "FSharp.Charting/lib/net40/FSharp.Charting.dll"
#r "FSharp.Control.AsyncSeq/lib/net45/FSharp.Control.AsyncSeq.dll"

open System
open System.IO
open System.Reactive.Linq
open System.Text.RegularExpressions
open System.Windows.Forms
open FSharp.Charting
open FSharp.Control

let isNotNull = function | null -> false | _ -> true

let matchLine pattern line =
    let m = Regex.Match(line, pattern)
    if m.Success then Some (Int32.Parse m.Groups.["value"].Value) else None

// TODO: something that works with log4net.Appender.RollingFileAppender
let changeNotifier logFile =
    async {
        let fileInfo = FileInfo logFile
        let watcher = new FileSystemWatcher(Path = fileInfo.DirectoryName,
                                            Filter = fileInfo.Name,
                                            EnableRaisingEvents = true)
        let! args = Async.AwaitEvent watcher.Changed
        return args.FullPath
    }

let rec readAllLines (reader:StreamReader) =
    seq {
        let line = reader.ReadLine()
        if isNotNull line then
            yield line
            yield! readAllLines reader
    }

let readNewLines filter path startPos =
    use file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
    use reader = new StreamReader(file)
    let newLength = reader.BaseStream.Length
    let lines = seq {
        if newLength > startPos then
            printfn "New content %d/%d" startPos newLength
            reader.BaseStream.Position <- startPos
            yield! reader |> readAllLines
    }
    (newLength, lines |> Seq.choose filter |> List.ofSeq)

let watch filter logChangeNotifier =
    let trace x = printfn "%O" x; x
    let generator (counter, pos) =
        async {
            let! logFile = logChangeNotifier
            let fileSize, lines = filter logFile pos
            let toPoint idx value = (counter + idx, value)
            let points = lines |> List.mapi toPoint
            return Some (points, (counter + points.Length, fileSize))
        }

    AsyncSeq.unfoldAsync generator (0, int64 0) |> AsyncSeq.concatSeq |> AsyncSeq.map trace

let drawChart pattern log =
    let logFilter = matchLine pattern |> readNewLines
    let logChangeNotifier = changeNotifier log
    let synchronize o = Observable.ObserveOn(o, WindowsFormsSynchronizationContext.Current)
    let throttle o = Observable.Throttle(o, TimeSpan.FromMilliseconds(float 200))
    let logChanges = watch logFilter logChangeNotifier

    logChanges |> AsyncSeq.toObservable |> throttle |> synchronize |> LiveChart.FastLineIncremental

let pattern, log =
    match fsi.CommandLineArgs with
    | [|_;pattern;file|] -> pattern, file
    | [|program|] -> failwithf "Usage: fsi.exe %s %s" program "<pattern> <file>"
    | _ -> failwith "unexpected"

printfn "Watched log file: %s\nPattern: %s" log pattern

let form = new Form(Visible = true, TopMost = true, Width = 700, Height = 500)
let chart = drawChart pattern log
let ctl = new FSharp.Charting.ChartTypes.ChartControl(chart, Dock = DockStyle.Fill)
form.Text <- log
form.Controls.Add(ctl)

[<STAThread>]
do Application.Run(form) 
﻿namespace Graphs
open System.Collections.Generic
open System.Linq
open System.IO
open System
open DrawGraph
open System.Linq
open Alea.CUDA

#nowarn "25"

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module DirectedGraph =

    let internal hasCuda = 
        lazy (
            try
                Device.Default.Name |> ignore
                true
            with
            _ ->    false
        )

    /// <summary>
    /// Instantiate a directed graph. Need number of vertices
    /// Format of the file:
    /// v -> a, b, c, d - v - unique vertex name for each line of the file. a, b, c, d - names of vertices it connects to.
    /// </summary>
    [<StructuredFormatDisplay("{AsEnumerable}")>]
    type DirectedGraph (nVertex : int, rowIndex : int seq, colIndex : int seq, verticesNameToOrdinal : IDictionary<string, int>) = 

        let rowIndex  = rowIndex.ToArray()
        let colIndex = colIndex.ToArray()
        let nEdges = rowIndex.[rowIndex.Length - 1]
        let nVertex = nVertex

        do
            if colIndex.Length <> nVertex then failwith "Inconsisten data. Edges <> vertices"

        let ordinalToNames () =
            let res : string [] = Array.zeroCreate verticesNameToOrdinal.Count
            verticesNameToOrdinal 
            |> Seq.iter (fun kvp -> res.[kvp.Value] <- kvp.Key)

            res

        let verticesNameToOrdinal = verticesNameToOrdinal
        let verticesOrdinalToNames = ordinalToNames()

        let nameFromOrdinal = fun ordinal -> verticesOrdinalToNames.[ordinal]
        let ordinalFromName = fun name -> verticesNameToOrdinal.[name]

        let getVertexConnections ordinal =
            let start = rowIndex.[ordinal]
            let end' = rowIndex.[ordinal + 1] - 1
            colIndex.[start..end']

        let asOrdinalsEnumerable () =
            Seq.init nVertex (fun i -> i, getVertexConnections i)

        let reverse =
            lazy (
                let allExistingRows = [0..rowIndex.Length - 1]                

                let grSeq = 
                    asOrdinalsEnumerable ()
                    |> Seq.map (fun (i, verts) -> verts |> Seq.map (fun v -> (v, i)))
                    |> Seq.collect id
                    |> Seq.groupBy fst
                    |> Seq.map (fun (key, sq) -> key, sq |> Seq.map snd |> Seq.toArray)

                let allRows : seq<int * int []> = 
                    allExistingRows.Except (grSeq |> Seq.map fst) |> Seq.map (fun e -> e, [||])
                    |> fun col -> col.Union grSeq
                    |> Seq.sortBy fst
                    

                let revRowIndex = allRows |> Seq.scan (fun st (key, v) -> st + v.Length) 0
                let revColIndex = allRows |> Seq.collect snd

                DirectedGraph(nVertex, revRowIndex, revColIndex, verticesNameToOrdinal)            
            )
                        
        /// <summary>
        /// Create the graph from an array of strings
        /// </summary>
        /// <param name="lines"></param>
        static member FromStrings (lines : string seq) =  

            let rowIndexRaw = List<int>()
            let colIndex = List<int>()
            let nameToOrdinal = Dictionary<string, int>() // vertices and the index to which they correspond

            let addVertex (line : string) =
                
                let vertex, connected = 
                    if line.Contains("->") then
                        line.Trim().Split([|"->"|], 2, StringSplitOptions.RemoveEmptyEntries) |> fun [|a; b|] -> a.Trim(), b.Trim()
                    else line.Trim(), ""

                if not (nameToOrdinal.ContainsKey vertex) then 
                    nameToOrdinal.Add(vertex, nameToOrdinal.Keys.Count)                    
                    rowIndexRaw.Add 0

                // get vertices connected to this one
                let connectedVertices =
                    if not (String.IsNullOrEmpty connected) then
                        connected.Split([|','|], StringSplitOptions.RemoveEmptyEntries)
                        |> Array.map (fun e -> e.Trim())
                    else [||]


                // store all the vertices we have not seen before
                let newVertices = connectedVertices.Except nameToOrdinal.Keys |> Seq.toArray
                newVertices
                |> Seq.iter (fun v -> nameToOrdinal.Add(v, nameToOrdinal.Keys.Count))
                
                // extend the new rows
                rowIndexRaw.AddRange (Array.zeroCreate newVertices.Length)

                // for now we will store the number of vertices in the row index
                // entry for the given row. We will need to scan it and update its values later
                rowIndexRaw.[nameToOrdinal.[vertex]] <- connectedVertices.Length

                connectedVertices
                |> Seq.map (fun v -> nameToOrdinal.[v])
                |> colIndex.AddRange

            lines |> Seq.iter addVertex

            DirectedGraph(rowIndexRaw.Count, rowIndexRaw |> Seq.scan (+) 0, colIndex, nameToOrdinal)


        /// <summary>
        /// Create the graph from a file
        /// </summary>
        /// <param name="fileName"></param>
        static member FromFile (fileName : string) =

            if String.IsNullOrWhiteSpace fileName || not (File.Exists fileName) then failwith "Invalid file"
            
            let lines = File.ReadLines(fileName)
            DirectedGraph.FromStrings(lines)                

        member this.Vertices = nVertex
        member this.Item
            with get vertex = ordinalFromName vertex |> getVertexConnections |> Array.map nameFromOrdinal

        member this.AsEnumerable = Seq.init nVertex (fun n -> nameFromOrdinal n, this.[nameFromOrdinal n])

        member this.Reverse = reverse.Force()
        member this.RowIndex = rowIndex
        member this.ColIndex = colIndex
        
        /// <summary>
        /// 0 -> 1 -> 2 -> 3 -> ... -> n -> 0
        /// </summary>
        /// <param name="n">Index of the last vertex</param>
        static member GenerateRandomLoop n =
            let dict = [0..n].ToDictionary((fun s -> s.ToString()), id)
            let rowIndex = [0..n + 1]
            let colIndex = [1..n] @ [0]
            DirectedGraph(n + 1, rowIndex, colIndex, dict)

        /// <summary>
        /// Visualize the graph. Should in/out connections be emphasized
        /// </summary>
        /// <param name="emphasizeInConnections">Optional. If present - should be the minimum number of inbound connections which would select the vertex for coloring.</param>
        /// <param name="emphasizeOutConnections">Optional. If present - should be the minimum number of outbound connections which would select the vertex for coloring.</param>
        member this.Visualize(?emphasizeInConnections, ?emphasizeOutConnections) =
            let outConMin = defaultArg emphasizeOutConnections 0
            let inConMin = defaultArg emphasizeInConnections 0
            
            let self = this.AsEnumerable
            let selfRev = this.Reverse.AsEnumerable

            let toColor (c : string) (vertices : seq<string>) =
                if vertices |> Seq.isEmpty then "" else
                let formatstr = 
                    let f = "{0} [style=filled, color={1}"
                    if c = "blue" then f + ", fontcolor=white]" else f + "]"
                vertices
                |> Seq.map (fun v -> String.Format(formatstr, v, c))
                |> Seq.reduce (+)
                

            let coloring in' out =
                let bottomOutgoing (graphSeq : seq<string * string []>) bottom = 
                    if bottom = 0 then Seq.empty
                    else
                        graphSeq
                        |> Seq.filter (fun (_, con) -> con.Length >= bottom)
                        |> Seq.map fst

                let outVertices = bottomOutgoing self out
                let inVertices = bottomOutgoing selfRev in'
                let outInVertices = inVertices.Intersect outVertices

                outVertices.Except outInVertices |> toColor "green",
                inVertices.Except outInVertices |> toColor "yellow",
                outInVertices |> toColor "blue"                   

            let colorOut, colorIn, colorBoth = coloring inConMin outConMin
            
            let visualizable = 
                self 
                |> Seq.map 
                    (fun (v, c) -> 
                        if c.Length = 0 then v
                        else
                        c 
                        |> Array.map (fun s -> v + " -> " + s)
                        |> Array.reduce (fun acc e -> acc + "; " + e))
                |> Seq.reduce (fun acc e -> acc + "; " + e)
                |> fun v -> "digraph {" + colorOut + colorIn + colorBoth + v + "}"

            createGraph visualizable None

            
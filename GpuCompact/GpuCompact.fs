﻿[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module GpuCompact

open Alea.CUDA
open Alea.CUDA.Unbound
open Alea.CUDA.Utilities

[<Kernel; ReflectedDefinition>]
let copyScanned (arr : deviceptr<int>) (out : deviceptr<int>) (len : int) (flags : deviceptr<int>) (addrmap : deviceptr<int>) =
    let ind = blockIdx.x * blockDim.x + threadIdx.x

    if ind < len && flags.[ind] > 0 then out.[addrmap.[ind] - 1] <- arr.[ind]

[<Kernel; ReflectedDefinition>]
let createMap (arr : deviceptr<int>) len (out : deviceptr<int>) =
    let ind = blockIdx.x * blockDim.x + threadIdx.x

    if ind < len then 
        out.[ind] <- if arr.[ind] <> 0 then 1 else 0
        
let internal worker = Worker.Default
let internal target = GPUModuleTarget.Worker worker
let internal blockSize = 512


let compactGpu (dArr : DeviceMemory<int>) =
    let origLen = dArr.Length
    let lp = LaunchParam(divup origLen blockSize, blockSize)
        
    use dMap = worker.Malloc(origLen)
    use dAddressMap = worker.Malloc(Array.zeroCreate origLen)

    worker.Launch <@createMap @> lp dArr.Ptr origLen dMap.Ptr 

    use scanModule = new DeviceScanModule<int>(GPUModuleTarget.Worker(worker), <@ (+) @>)
    use scanner = scanModule.Create(origLen)

    scanner.InclusiveScan(dMap.Ptr, dAddressMap.Ptr, origLen)
    let len = dAddressMap.GatherScalar(origLen - 1)
    let dCompacted = worker.Malloc<int>(len)


    worker.Launch <@copyScanned@> lp dArr.Ptr dCompacted.Ptr origLen dMap.Ptr dAddressMap.Ptr
    dCompacted

let compact (arr : int []) = 
    use dArr = worker.Malloc(arr)
    compactGpu dArr |> fun o -> o.Gather()

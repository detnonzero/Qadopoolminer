# Qadopoolminer

`Qadopoolminer` is a Windows WPF desktop miner for Qado pool mining. It connects to a Qado pool, manages a local Ed25519 miner key, accepts a miner API token from the pool dashboard, and mines with selected OpenCL devices.

## Features

- pool connection testing and job loading
- local miner key generation and challenge signing
- miner token acceptance and validation
- OpenCL device discovery and GPU mining
- live mining stats, logs, and pool feedback
- self-contained Windows release builds via `release.ps1`

## Requirements

- Windows
- .NET SDK 10 for local development
- an OpenCL-capable GPU and working OpenCL runtime/driver
- a running Qado pool endpoint

## Quick Start

1. Enter the pool URL and test the connection.
2. Generate or paste a miner private key and accept it.
3. Copy the miner public key into the pool dashboard and complete the verification challenge.
4. Paste the issued miner API token into the miner and accept it.
5. Refresh OpenCL devices, select the devices you want to use, then start mining.

The miner stores its local configuration in `settings.json` next to the executable.

## Development

```powershell
dotnet build .\Qadopoolminer.sln
```

## Release Build

```powershell
powershell -ExecutionPolicy Bypass -File .\release.ps1 -Clean
```

The release output is written to `.\release\win-x64`.

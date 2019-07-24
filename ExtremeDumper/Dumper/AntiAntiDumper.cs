using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using dnlib.DotNet;
using dnlib.IO;
using dnlib.PE;
using ExtremeDumper.AntiAntiDump;
using Microsoft.Diagnostics.Runtime;
using NativeSharp;
using ImageLayout = dnlib.PE.ImageLayout;

namespace ExtremeDumper.Dumper {
	public sealed unsafe class AntiAntiDumper : IDumper {
		#region .net structs
		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		private struct IMAGE_DATA_DIRECTORY {
			public static readonly uint UnmanagedSize = (uint)sizeof(IMAGE_DATA_DIRECTORY);

			public uint VirtualAddress;
			public uint Size;
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		private struct IMAGE_COR20_HEADER {
			public static readonly uint UnmanagedSize = (uint)sizeof(IMAGE_COR20_HEADER);

			public uint cb;
			public ushort MajorRuntimeVersion;
			public ushort MinorRuntimeVersion;
			public IMAGE_DATA_DIRECTORY MetaData;
			public uint Flags;
			public uint EntryPointTokenOrRVA;
			public IMAGE_DATA_DIRECTORY Resources;
			public IMAGE_DATA_DIRECTORY StrongNameSignature;
			public IMAGE_DATA_DIRECTORY CodeManagerTable;
			public IMAGE_DATA_DIRECTORY VTableFixups;
			public IMAGE_DATA_DIRECTORY ExportAddressTableJumps;
			public IMAGE_DATA_DIRECTORY ManagedNativeHeader;
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		private struct STORAGESIGNATURE {
			/// <summary>
			/// 大小不包括pVersion的长度
			/// </summary>
			public static readonly uint UnmanagedSize = (uint)sizeof(STORAGESIGNATURE) - 1;

			public uint lSignature;
			public ushort iMajorVer;
			public ushort iMinorVer;
			public uint iExtraData;
			public uint iVersionString;
			/// <summary>
			/// 由于C#语法问题不能写pVersion[0]，实际长度由 <see cref="iVersionString"/> 决定
			/// </summary>
			public fixed byte pVersion[1];
		}

		[StructLayout(LayoutKind.Sequential, Pack = 1)]
		private struct STORAGEHEADER {
			public static readonly uint UnmanagedSize = (uint)sizeof(STORAGEHEADER);

			public byte fFlags;
			public byte pad;
			public ushort iStreams;
		}
		#endregion

		private uint _processId;

		private AntiAntiDumper() {
		}

		public static IDumper Create(uint processId) {
			return new AntiAntiDumper() {
				_processId = processId
			};
		}

		public bool DumpModule(IntPtr moduleHandle, ImageLayout imageLayout, string filePath) {
			ClrModule dacModule;
			InjectionClrVersion clrVersion;
			Injection.Options options;
			AntiAntiDumpService antiAntiDumpService;
			AntiAntiDumpInfo antiAntiDumpInfo;
			MetadataInfo metadataInfo;
			byte[] peImageData;

			dacModule = TryGetDacModule(moduleHandle);
			if (dacModule == null)
				return false;
			switch (dacModule.Runtime.ClrInfo.Version.Major) {
			case 2:
				clrVersion = InjectionClrVersion.V2;
				break;
			case 4:
				clrVersion = InjectionClrVersion.V4;
				break;
			default:
				return false;
			}
			// 判断要dump的模块的CLR版本
			options = new Injection.Options {
				PortName = Guid.NewGuid().ToString(),
				ObjectName = Guid.NewGuid().ToString()
			};
			using (NativeProcess process = NativeProcess.Open(_processId))
				if (!process.InjectManaged(typeof(AntiAntiDumpService).Assembly.Location, typeof(Injection).FullName, "Main", options.Serialize(), clrVersion, out int result) || result != 0)
					return false;
			antiAntiDumpService = (AntiAntiDumpService)Activator.GetObject(typeof(AntiAntiDumpService), $"Ipc://{options.PortName}/{options.ObjectName}");
			// 注入DLL，通过.NET Remoting获取AntiAntiDumpService实例
			antiAntiDumpInfo = antiAntiDumpService.GetAntiAntiDumpInfo(moduleHandle);
			if (!antiAntiDumpInfo.CanAntiAntiDump)
				return false;
			imageLayout = (ImageLayout)antiAntiDumpInfo.ImageLayout;
			// 覆盖通过DAC获取的，不确定DAC获取的是否准确，毕竟DAC的bug还不少
			metadataInfo = antiAntiDumpInfo.MetadataInfo;
			PrintStreamInfo("#~ or #-", metadataInfo.TableStream);
			PrintStreamInfo("#Strings", metadataInfo.StringHeap);
			PrintStreamInfo("#US", metadataInfo.UserStringHeap);
			PrintStreamInfo("#GUID", metadataInfo.GuidHeap);
			PrintStreamInfo("#Blob", metadataInfo.BlobHeap);
			peImageData = PEImageHelper.DirectCopy(_processId, (void*)moduleHandle, imageLayout);
			FixHeader(peImageData, antiAntiDumpInfo);
			peImageData = PEImageHelper.ConvertImageLayout(peImageData, imageLayout, ImageLayout.File);
			File.WriteAllBytes(filePath, peImageData);
			return true;
		}

		private static void PrintStreamInfo(string name, MetadataStreamInfo streamInfo) {
			Debug.WriteLine($"Name: {name}");
			if (streamInfo is null) {
				Debug.WriteLine("Not exists.");
			}
			else {
				Debug.WriteLine($"Rva: 0x{streamInfo.Rva.ToString("X8")}");
				Debug.WriteLine($"Length: 0x{streamInfo.Length.ToString("X8")}");
			}
			Debug.WriteLine(string.Empty);
		}

		public int DumpProcess(string directoryPath) {
			throw new NotSupportedException();
		}

		private void FixHeader(byte[] peImageData, AntiAntiDumpInfo antiAntiDumpInfo) {
			ImageLayout imageLayout;
			uint cor20HeaderRva;
			uint metadataRva;
			uint metadataSize;
			MetadataInfo metadataInfo;
			MetadataStreamInfo tableStreamInfo;
			MetadataStreamInfo stringHeapInfo;
			MetadataStreamInfo userStringHeapInfo;
			MetadataStreamInfo guidHeapInfo;
			MetadataStreamInfo blobHeapInfo;

			imageLayout = (ImageLayout)antiAntiDumpInfo.ImageLayout;
			cor20HeaderRva = antiAntiDumpInfo.Cor20HeaderRva;
			metadataRva = antiAntiDumpInfo.MetadataRva;
			metadataSize = antiAntiDumpInfo.MetadataSize;
			metadataInfo = antiAntiDumpInfo.MetadataInfo;
			tableStreamInfo = metadataInfo.TableStream;
			stringHeapInfo = metadataInfo.StringHeap;
			userStringHeapInfo = metadataInfo.UserStringHeap;
			guidHeapInfo = metadataInfo.GuidHeap;
			blobHeapInfo = metadataInfo.BlobHeap;
			using (PEImage peHeader = new PEImage(peImageData, ImageLayout.File, false)) {
				// 用于转换RVA与FOA，必须指定imageLayout参数为ImageLayout.File
				switch (imageLayout) {
				case ImageLayout.File:
					peImageData = PEImageHelper.ConvertImageLayout(peImageData, imageLayout, ImageLayout.Memory);
					cor20HeaderRva = (uint)peHeader.ToRVA((FileOffset)cor20HeaderRva);
					metadataRva = (uint)peHeader.ToRVA((FileOffset)metadataRva);
					break;
				case ImageLayout.Memory:
					break;
				default:
					throw new NotSupportedException();
				}
				fixed (byte* p = peImageData) {
					IMAGE_DATA_DIRECTORY* pNETDirectory;
					IMAGE_COR20_HEADER* pCor20Header;
					STORAGESIGNATURE* pStorageSignature;
					byte[] versionString;
					STORAGEHEADER* pStorageHeader;
					uint* pStreamHeader;

					pNETDirectory = (IMAGE_DATA_DIRECTORY*)(p + (uint)peHeader.ImageNTHeaders.OptionalHeader.DataDirectories[14].StartOffset);
					pNETDirectory->VirtualAddress = cor20HeaderRva;
					pNETDirectory->Size = IMAGE_COR20_HEADER.UnmanagedSize;
					// Set Data Directories
					pCor20Header = (IMAGE_COR20_HEADER*)(p + cor20HeaderRva);
					pCor20Header->cb = IMAGE_COR20_HEADER.UnmanagedSize;
					pCor20Header->MajorRuntimeVersion = 0x2;
					pCor20Header->MinorRuntimeVersion = 0x5;
					pCor20Header->MetaData.VirtualAddress = metadataRva;
					pCor20Header->MetaData.Size = metadataSize;
					// Set .NET Directory
					pStorageSignature = (STORAGESIGNATURE*)(p + metadataRva);
					pStorageSignature->lSignature = 0x424A5342;
					pStorageSignature->iMajorVer = 0x1;
					pStorageSignature->iMinorVer = 0x1;
					pStorageSignature->iExtraData = 0x0;
					pStorageSignature->iVersionString = 0xC;
					versionString = Encoding.ASCII.GetBytes("v4.0.30319");
					for (int i = 0; i < versionString.Length; i++)
						pStorageSignature->pVersion[i] = versionString[i];
					// versionString仅仅占位用，程序集具体运行时版本用dnlib获取
					// Set StorageSignature
					pStorageHeader = (STORAGEHEADER*)((byte*)pStorageSignature + STORAGESIGNATURE.UnmanagedSize + pStorageSignature->iVersionString);
					pStorageHeader->fFlags = 0x0;
					pStorageHeader->pad = 0x0;
					pStorageHeader->iStreams = 0x5;
					// Set StorageHeader
					pStreamHeader = (uint*)((byte*)pStorageHeader + STORAGEHEADER.UnmanagedSize);
					if (tableStreamInfo != null) {
						*pStreamHeader = imageLayout == ImageLayout.Memory ? tableStreamInfo.Rva : (uint)peHeader.ToRVA((FileOffset)tableStreamInfo.Rva);
						*pStreamHeader -= metadataRva;
						pStreamHeader++;
						*pStreamHeader = tableStreamInfo.Length;
						pStreamHeader++;
						*pStreamHeader = 0x00007E23;
						// #~ 暂时不支持#-表流的程序集
						pStreamHeader++;
					}
					if (stringHeapInfo != null) {
						*pStreamHeader = imageLayout == ImageLayout.Memory ? stringHeapInfo.Rva : (uint)peHeader.ToRVA((FileOffset)stringHeapInfo.Rva);
						*pStreamHeader -= metadataRva;
						pStreamHeader++;
						*pStreamHeader = stringHeapInfo.Length;
						pStreamHeader++;
						*pStreamHeader = 0x72745323;
						pStreamHeader++;
						*pStreamHeader = 0x73676E69;
						pStreamHeader++;
						*pStreamHeader = 0x00000000;
						pStreamHeader++;
						// #Strings
					}
					if (userStringHeapInfo != null) {
						*pStreamHeader = imageLayout == ImageLayout.Memory ? userStringHeapInfo.Rva : (uint)peHeader.ToRVA((FileOffset)userStringHeapInfo.Rva);
						*pStreamHeader -= metadataRva;
						pStreamHeader++;
						*pStreamHeader = userStringHeapInfo.Length;
						pStreamHeader++;
						*pStreamHeader = 0x00535523;
						pStreamHeader++;
						// #US
					}
					if (guidHeapInfo != null) {
						*pStreamHeader = imageLayout == ImageLayout.Memory ? guidHeapInfo.Rva : (uint)peHeader.ToRVA((FileOffset)guidHeapInfo.Rva);
						*pStreamHeader -= metadataRva;
						pStreamHeader++;
						*pStreamHeader = guidHeapInfo.Length;
						pStreamHeader++;
						*pStreamHeader = 0x49554723;
						pStreamHeader++;
						*pStreamHeader = 0x00000044;
						pStreamHeader++;
						// #GUID
					}
					if (blobHeapInfo != null) {
						*pStreamHeader = imageLayout == ImageLayout.Memory ? blobHeapInfo.Rva : (uint)peHeader.ToRVA((FileOffset)blobHeapInfo.Rva);
						*pStreamHeader -= metadataRva;
						pStreamHeader++;
						*pStreamHeader = blobHeapInfo.Length;
						pStreamHeader++;
						*pStreamHeader = 0x6F6C4223;
						pStreamHeader++;
						*pStreamHeader = 0x00000062;
						pStreamHeader++;
						// #GUID
					}
				}
			}
			using (ModuleDefMD moduleDef = ModuleDefMD.Load(new PEImage(peImageData, imageLayout, false)))
				fixed (byte* p = peImageData) {
					STORAGESIGNATURE* pStorageSignature;
					byte[] versionString;

					pStorageSignature = (STORAGESIGNATURE*)(p + metadataRva);
					switch (moduleDef.CorLibTypes.AssemblyRef.Version.Major) {
					case 2:
						versionString = Encoding.ASCII.GetBytes("v2.0.50727");
						break;
					case 4:
						versionString = Encoding.ASCII.GetBytes("v4.0.30319");
						break;
					default:
						throw new NotSupportedException();
					}
					for (int i = 0; i < versionString.Length; i++)
						pStorageSignature->pVersion[i] = versionString[i];
				}
		}

		private ClrModule TryGetDacModule(IntPtr moduleHandle) {
			DataTarget dataTarget;

			try {
				using (dataTarget = DataTarget.AttachToProcess((int)_processId, 3000, AttachFlag.Passive))
					return dataTarget.ClrVersions.SelectMany(t => t.CreateRuntime().Modules).First(t => (IntPtr)t.ImageBase == moduleHandle);
			}
			catch {
			}
			return null;
		}

		public void Dispose() {
		}
	}
}
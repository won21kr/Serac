﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using static System.Console;

namespace Serac.WebSockets {
	public class WebSocket {
		internal class CloseException : Exception {}
		readonly Stream Stream;

		public event EventHandler<bool> Disconnect;
		bool Closed, ClientClose;
		
		internal WebSocket(Stream stream) => Stream = stream;

		public async Task<string> ReadText() {
			var ret = "";
			while(true) {
				var (final, opcode, data) = await ReadFrame();
				ret += Encoding.UTF8.GetString(data);
				if(final)
					break;
			}
			return ret;
		}

		public async Task<byte[]> ReadBinary() {
			var ret = new List<byte[]>();
			while(true) {
				var (final, opcode, data) = await ReadFrame();
				ret.Add(data);
				if(final)
					break;
			}
			return ret.SelectMany(x => x).ToArray();
		}

		public async Task Write(string message) {
			var data = Encoding.UTF8.GetBytes(message);
			for(var i = 0; i < data.Length; i += 32768)
				await WriteFrame(i + 32768 >= data.Length, i == 0 ? 1 : 0, data.Skip(i).Take(32768).ToArray());
		}

		public async Task Write(byte[] data) {
			for(var i = 0; i < data.Length; i += 32768)
				await WriteFrame(i + 32768 >= data.Length, i == 0 ? 2 : 0, data.Skip(i).Take(32768).ToArray());
		}

		async Task<(bool, int, byte[])> ReadFrame() {
			var start = await Stream.ReadAsync(2);
			var final = start[0] >> 7 == 1;
			var opcode = start[0] & 0xF;
			if(opcode == 8) {
				ClientClose = true;
				throw new CloseException();
			}

			var plen = start[1] & 0x7F;
			if(plen == 126) {
				var slen = await Stream.ReadAsync(2);
				plen = (slen[0] << 8) | slen[1];
			} else if(plen == 127) {
				var slen = await Stream.ReadAsync(8);
				Array.Reverse(slen);
				var temp = BitConverter.ToUInt64(slen, 0);
				if(temp > int.MaxValue) throw new OverflowException();
				plen = (int) temp;
			}

			var mask = start[1] >> 7 == 1 ? await Stream.ReadAsync(4) : null;
			var data = await Stream.ReadAsync(plen);

			if(mask != null)
				for(var i = 0; i < plen; ++i)
					data[i] ^= mask[i & 3];

			if(opcode == 9) {
				await WriteFrame(final, 10, data);
				return await ReadFrame();
			}
			
			return (final, opcode, data);
		}

		async Task WriteFrame(bool final, int opcode, byte[] data) {
			await Stream.WriteAsync(new byte[] { (byte) (opcode | ((final ? 1 : 0) << 7)), (byte) (data.Length < 126 ? data.Length : 126) });
			if(data.Length >= 126)
				await Stream.WriteAsync(BitConverter.GetBytes((ushort) data.Length));
			await Stream.WriteAsync(data);
			await Stream.FlushAsync();
		}

		public async Task Close() {
			if(!Closed) {
				Closed = true;
				await WriteFrame(true, 8, new byte[0]);
				Disconnect?.Invoke(this, ClientClose);
				Stream.Close();
				throw new CloseException();
			}
		}
	}
	
	public static class WebSockets {
		public static Func<Request, Task<Response>> Serve(Func<WebSocket, Request, Task> handler) {
			return async request => {
				if(request.Method != "GET" || !request.Headers.ContainsKey("Connection") ||
				   request.Headers["Connection"] != "Upgrade")
					return null;
				
				await new Response {
					StatusCode = 101,
					Headers = {
						["Upgrade"] = "websocket",
						["Connection"] = "Upgrade",
						["Sec-WebSocket-Accept"] = Convert.ToBase64String(SHA1.Create().ComputeHash(
							Encoding.ASCII.GetBytes(request.Headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))), 
						["Sec-WebSocket-Protocol"] = request.Headers.TryGetValue("Sec-WebSocket-Protocol")
					}
				}.Send(request.Stream, request.StreamWriter);
				
				var ws = new WebSocket(request.Stream);
				try {
					await handler(ws, request);
				} catch(WebSocket.CloseException) {
					try {
						await ws.Close();
					} catch(WebSocket.CloseException) {
						// Expected
					}
				}

				return null;
			};
		}

		public static Func<Request, Task<Response>> Serve(Func<WebSocket, Task> handler) =>
			Serve((ws, request) => handler(ws));

		public static WebServer WebSocket(this WebServer server, Func<WebSocket, Request, Task> handler, string path) =>
			server.RegisterHandler(Serve(handler), path);

		public static WebServer WebSocket(this WebServer server, Func<WebSocket, Task> handler, string path) =>
			server.RegisterHandler(Serve(handler), path);
	}
}
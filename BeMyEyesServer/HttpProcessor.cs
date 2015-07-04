using System;
using System.Collections;
using System.IO;
using System.Net.Sockets;
using System.Threading;

// offered to the public domain for any use with no restriction
// and also with no warranty of any kind, please enjoy. - David Jeske. 

// simple HTTP explanation
// http://www.jmarshall.com/easy/http/

namespace SmartEyesServer
{

    public class HttpProcessor
    {
        private readonly TcpClient _socket;
        private readonly HttpServer _srv;

        private Stream _inputStream;
        public StreamWriter OutputStream;

        private String _httpMethod;
        public String HttpUrl;
        // ReSharper disable once NotAccessedField.Local
        private String _httpProtocolVersionstring;
        private readonly Hashtable _httpHeaders = new Hashtable();


        private const int MaxPostSize = 10 * 1024 * 1024; // 10MB

        public HttpProcessor(TcpClient s, HttpServer srv)
        {
            this._socket = s;
            this._srv = srv;
        }


        private string StreamReadLine(Stream inputStream)
        {
            string data = "";
            while (true)
            {
                var nextChar = inputStream.ReadByte();
                if (nextChar == '\n') { break; }
                if (nextChar == '\r') { continue; }
                if (nextChar == -1) { Thread.Sleep(1); continue; }
                data += Convert.ToChar(nextChar);
            }
            return data;
        }
        public void Process()
        {
            // we can't use a StreamReader for input, because it buffers up extra data on us inside it's
            // "processed" view of the world, and we want the data raw after the headers
            _inputStream = new BufferedStream(_socket.GetStream());

            // we probably shouldn't be using a streamwriter for all output from handlers either
            OutputStream = new StreamWriter(new BufferedStream(_socket.GetStream()));
            try
            {
                ParseRequest();
                ReadHeaders();
                if (_httpMethod.Equals("GET"))
                {
                    HandleGetRequest();
                }
                else if (_httpMethod.Equals("POST"))
                {
                    HandlePostRequest();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception: " + e);
                WriteFailure();
            }
            OutputStream.Flush();
            // bs.Flush(); // flush any remaining output
            _inputStream = null; OutputStream = null; // bs = null;            
            _socket.Close();
        }

        private void ParseRequest()
        {
            String request = StreamReadLine(_inputStream);
            string[] tokens = request.Split(' ');
            if (tokens.Length != 3)
            {
                throw new Exception("invalid http request line");
            }
            _httpMethod = tokens[0].ToUpper();
            HttpUrl = tokens[1];
            _httpProtocolVersionstring = tokens[2];

            Console.WriteLine("starting: " + request);
        }

        private void ReadHeaders()
        {
            Console.WriteLine("readHeaders()");
            String line;
            while ((line = StreamReadLine(_inputStream)) != null)
            {
                if (line.Equals(""))
                {
                    Console.WriteLine("got headers");
                    return;
                }

                int separator = line.IndexOf(':');
                if (separator == -1)
                {
                    throw new Exception("invalid http header line: " + line);
                }
                String name = line.Substring(0, separator);
                int pos = separator + 1;
                while ((pos < line.Length) && (line[pos] == ' '))
                {
                    pos++; // strip any spaces
                }

                string value = line.Substring(pos, line.Length - pos);
                Console.WriteLine("header: {0}:{1}", name, value);
                _httpHeaders[name] = value;
            }
        }

        private void HandleGetRequest()
        {
            _srv.HandleGetRequest(this);
        }

        private const int BufSize = 4096;

        private void HandlePostRequest()
        {
            // this post data processing just reads everything into a memory stream.
            // this is fine for smallish things, but for large stuff we should really
            // hand an input stream to the request processor. However, the input stream 
            // we hand him needs to let him see the "end of the stream" at this content 
            // length, because otherwise he won't know when he's seen it all! 

            Console.WriteLine("get post data start");
            int contentLen;
            MemoryStream ms = new MemoryStream();
            if (this._httpHeaders.ContainsKey("Content-Length"))
            {
                contentLen = Convert.ToInt32(this._httpHeaders["Content-Length"]);
                if (contentLen > MaxPostSize)
                {
                    throw new Exception(
                        String.Format("POST Content-Length({0}) too big for this simple server",
                          contentLen));
                }
                byte[] buf = new byte[BufSize];
                int toRead = contentLen;
                while (toRead > 0)
                {
                    Console.WriteLine("starting Read, to_read={0}", toRead);

                    int numread = this._inputStream.Read(buf, 0, Math.Min(BufSize, toRead));
                    Console.WriteLine("read finished, numread={0}", numread);
                    if (numread == 0)
                    {
                        if (toRead == 0)
                        {
                            break;
                        }
                        else
                        {
                            throw new Exception("client disconnected during post");
                        }
                    }
                    toRead -= numread;
                    ms.Write(buf, 0, numread);
                }
                ms.Seek(0, SeekOrigin.Begin);
            }
            Console.WriteLine("get post data end");
            _srv.HandlePostRequest(this, new StreamReader(ms));

        }

        public void WriteSuccess(string contentType = "text/html")
        {
            // this is the successful HTTP response line
            OutputStream.WriteLine("HTTP/1.0 200 OK");
            // these are the HTTP headers...          
            OutputStream.WriteLine("Content-Type: " + contentType);
            OutputStream.WriteLine("Connection: close");
            // ..add your own headers here if you like

            OutputStream.WriteLine(""); // this terminates the HTTP headers.. everything after this is HTTP body..
        }

        private void WriteFailure()
        {
            // this is an http 404 failure response
            OutputStream.WriteLine("HTTP/1.0 404 File not found");
            // these are the HTTP headers
            OutputStream.WriteLine("Connection: close");
            // ..add your own headers here

            OutputStream.WriteLine(""); // this terminates the HTTP headers.
        }
    }

    public abstract class HttpServer
    {
        private readonly int _port;
        TcpListener _listener;

        protected HttpServer(int port)
        {
            this._port = port;
        }

        public void Listen()
        {
#pragma warning disable 618
            _listener = new TcpListener(_port);
#pragma warning restore 618
            _listener.Start();
            while (true)
            {
                TcpClient s = _listener.AcceptTcpClient();
                HttpProcessor processor = new HttpProcessor(s, this);
                Thread thread = new Thread(processor.Process);
                thread.Start();
                Thread.Sleep(1);
            }
            // ReSharper disable once FunctionNeverReturns
        }

        public abstract void HandleGetRequest(HttpProcessor p);
        public abstract void HandlePostRequest(HttpProcessor p, StreamReader inputData);
    }

    public class MyHttpServer : HttpServer
    {
        public MyHttpServer(int port)
            : base(port)
        {
        }
        public override void HandleGetRequest(HttpProcessor p)
        {

            if (p.HttpUrl.Equals("/Test.mp3"))
            {
                Stream fs = File.Open("../../Test.mp3", FileMode.Open);

                p.WriteSuccess("Go!");
                fs.CopyTo(p.OutputStream.BaseStream);
                p.OutputStream.BaseStream.Flush();
            }

            Console.WriteLine("request: {0}", p.HttpUrl);
            p.WriteSuccess();
            p.OutputStream.WriteLine("<html><body><h1>test server</h1>");
            p.OutputStream.WriteLine("Current Time: " + DateTime.Now);
            p.OutputStream.WriteLine("url : {0}", p.HttpUrl);

            p.OutputStream.WriteLine("<form method=post action=/form>");
            p.OutputStream.WriteLine("<input type=text name=foo value=foovalue>");
            p.OutputStream.WriteLine("<input type=submit name=bar value=barvalue>");
            p.OutputStream.WriteLine("</form>");
        }

        public override void HandlePostRequest(HttpProcessor p, StreamReader inputData)
        {
            Console.WriteLine("POST request: {0}", p.HttpUrl);
            string data = inputData.ReadToEnd();

            p.WriteSuccess();
            p.OutputStream.WriteLine("<html><body><h1>test server</h1>");
            p.OutputStream.WriteLine("<a href=/test>return</a><p>");
            p.OutputStream.WriteLine("postbody: <pre>{0}</pre>", data);


        }
    }

    public static class TestMain
    {
        public static int Main(String[] args)
        {
            HttpServer httpServer;
            if (args.GetLength(0) > 0)
            {
                httpServer = new MyHttpServer(Convert.ToInt16(args[0]));
            }
            else
            {
                httpServer = new MyHttpServer(8080);
            }
            Thread thread = new Thread(httpServer.Listen);
            //thread.IsBackground = true;
            thread.Start();
            return 0;
        }

    }

}
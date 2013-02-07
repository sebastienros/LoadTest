using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace LoadTest
{
    class Program
    {
        static readonly object SynLock = new object();
        static readonly List<Response> Responses = new List<Response>();

        static void Main(string[] args)
        {
            string url = null;
            var threads = 10; // concurrent threads to launch
            var warmup = 0; // warmup time before computing performance metrics (seconds)
            var think = 0; // delay between two requests for a single thread (ms)

            if (args.Length > 0)
            {
                url = args[0];
            }

            if (args.Length > 1)
            {
                threads = Convert.ToInt32(args[1]);
            }

            if (args.Length > 2)
            {
                warmup = Convert.ToInt32(args[2]);
            }

            if (args.Length > 3)
            {
                think = Convert.ToInt32(args[3]);
            }

            if (url == null)
            {
                Console.WriteLine("Usage: loadtest.exe url [threads] [warmup (s)] [think (ms)]");
                return;
            }

            // removing default connections limit
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, threads);

            var tokenSource = new CancellationTokenSource();
            var token = tokenSource.Token;

            var start = DateTime.UtcNow;

            Console.WriteLine("Starting {0} threads", threads);

            var tasks = Enumerable.Range(0, threads)
                .Select(x => Task.Run(
                    () => Stress(new RequestContext
                    {
                        Url = url,
                        TaskId = x,
                        Think = think,
                        CancellationToken = token
                    },
                    response =>
                    {
                        lock (SynLock)
                        {
                            Responses.Add(response);
                        }
                    }
                    ))
                ).ToArray();
            
            Console.WriteLine("Press any key to cancel the test.");
            var cursorTop = Console.CursorTop;

            while (!Console.KeyAvailable)
            {
                IList<Response> responses;

                // filter warmup responses
                lock (SynLock)
                {
                    responses = Responses.Where(x => x.StartUtc >= start.AddSeconds(warmup)).ToList();
                }

                if (responses.Any())
                {
                    Console.SetCursorPosition(0, cursorTop);
                    DisplayStats(responses);
                }
            }

            tokenSource.Cancel();
            Console.WriteLine();
            Console.WriteLine("Task cancellation requested.");

            // optional, wait for tasks, but then catch is mandatory
            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException e)
            {
                //foreach (var v in e.InnerExceptions)
                //    Console.WriteLine("msg: " + v.Message);
            }
        }

        static void DisplayStats(IList<Response> responses)
        {
            if (!responses.Any())
            {
                return;
            }

            var average = responses.Average(x => (x.EndUtc - x.StartUtc).TotalMilliseconds);
            ClearLine();
            Console.WriteLine("Average time: {0} ms", Convert.ToInt32(average));

            var min = responses.Min(x => x.StartUtc);
            var max = responses.Max(x => x.EndUtc);
            var count = responses.Count;
            var timespan = Convert.ToInt32((max - min).TotalMilliseconds);
            timespan = timespan == 0 ? 0 : timespan / 1000;
            var rps = timespan == 0 ? 0 : count / timespan;

            ClearLine();
            Console.WriteLine("Performance: {0} rps ({1} reqs in {2})", Convert.ToInt32(rps), responses.Count, timespan);

            ClearLine();
            Console.WriteLine("Threads: {0}", responses.Select(x => x.TaskId).Distinct().Count());

            ClearLine();
            Console.WriteLine("Errors: {0}", responses.Count(x => !x.Success));

        }

        static void Stress(RequestContext context, Action<Response> callback)
        {
            // cancellation already requested? 
            if (context.CancellationToken.IsCancellationRequested)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            while(true) 
            {
                var response = Request(context.Url);
                response.TaskId = context.TaskId;
                response.Url = context.Url;

                callback(response);

                // cancellation requested? 
                if (context.CancellationToken.IsCancellationRequested)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                }

                // pause between two requests
                if (context.Think > 0)
                {
                    Thread.Sleep(context.Think);
                }
            }
        }

        static Response Request(string url)
        {
            var wc = new WebClient();
            var response = new Response();

            try
            {
                response.StartUtc = DateTime.UtcNow;
                wc.DownloadString(url);
                response.EndUtc = DateTime.UtcNow;
                response.Success = true;
            }
            catch(WebException we)
            {
                response.Content = we.Response.ToString();
                response.Success = false;
            }

            return response;
        }

        static void ClearLine()
        {
            Console.WriteLine();
            Console.SetCursorPosition(0, Console.CursorTop - 1);

        }
    }

    class RequestContext
    {
        public string Url { get; set; }
        public int TaskId { get; set; }
        public int Think { get; set; }
        public CancellationToken CancellationToken { get; set; }
    }

    class Response
    {
        public bool Success { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public int TaskId { get; set; }
        public string Url { get; set; }

        // Set if Success is false
        public string Content { get; set; }

        public TimeSpan Ellapsed { get { return EndUtc - StartUtc; } }
    }
}

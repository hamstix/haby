#!/usr/bin/env python3
"""Fixture-owned transparent byte relay; no retries, pooling or protocol inspection."""
import argparse, asyncio, datetime, json, os, pathlib, signal, socket

def utc(): return datetime.datetime.now(datetime.timezone.utc).isoformat()

async def main(args):
    ready=pathlib.Path(args.ready); events=pathlib.Path(args.events)
    if ready.exists() or events.exists(): raise RuntimeError('Use fresh owned state paths')
    events.parent.mkdir(parents=True,exist_ok=True)
    os.umask(0o077)
    clients=set(); shutdown=asyncio.Event(); sequence=0
    def event(kind, **fields):
        with events.open('a') as f: f.write(json.dumps(dict(utc=utc(),runId=args.run_id,fixtureId=args.fixture_id,event=kind,**fields))+'\n')
    async def copy(reader,writer):
        while True:
            block=await reader.read(65536)
            if not block:
                if writer.can_write_eof(): writer.write_eof(); await writer.drain()
                return
            writer.write(block); await writer.drain()
    async def handle(reader,writer):
        nonlocal sequence
        task=asyncio.current_task(); clients.add(task); sequence+=1; number=sequence; upstream=None
        event('accepted',connection=number)
        try:
            # No retries, pooling, TLS termination or authentication/SQL inspection.
            upstream_reader,upstream=await asyncio.wait_for(asyncio.open_connection(args.upstream_host,args.upstream_port),timeout=args.connect_timeout_seconds)
            event('upstreamConnected',connection=number,configuredOwnedTarget=True)
            await asyncio.gather(copy(reader,upstream),copy(upstream_reader,writer))
        except OSError as error:
            event('socketFailure',connection=number,exceptionType=type(error).__name__,errno=error.errno,configuredOwnedTarget=True)
        except asyncio.CancelledError:
            event('cancelledDuringOwnedCleanup',connection=number)
            raise
        finally:
            for stream in (writer,upstream):
                if stream is not None:
                    stream.close()
                    try: await stream.wait_closed()
                    except (OSError,asyncio.CancelledError): pass
            clients.discard(task); event('closed',connection=number)
    ipv4=await asyncio.start_server(handle,'127.0.0.1',0,family=socket.AF_INET,start_serving=False)
    port=ipv4.sockets[0].getsockname()[1]
    ipv6=None
    try:
        ipv6=await asyncio.start_server(handle,'::1',port,family=socket.AF_INET6,start_serving=False)
        addresses={item[4][0] for item in socket.getaddrinfo('localhost',port,type=socket.SOCK_STREAM)}
        if not addresses or not addresses <= {'127.0.0.1','::1'}: raise RuntimeError('localhost does not resolve exclusively to owned loopback listeners')
        await ipv4.start_serving(); await ipv6.start_serving()
        ready.write_text(json.dumps(dict(runId=args.run_id,fixtureId=args.fixture_id,pid=os.getpid(),port=port,loopbackFamilies=['IPv4','IPv6'],startedUtc=utc()))+'\n')
        event('ready',loopbackFamilies=['IPv4','IPv6'])
        loop=asyncio.get_running_loop()
        for sig in (signal.SIGTERM,signal.SIGINT): loop.add_signal_handler(sig,shutdown.set)
        await shutdown.wait()
    finally:
        ipv4.close(); await ipv4.wait_closed()
        if ipv6 is not None: ipv6.close(); await ipv6.wait_closed()
        remaining=list(clients)
        for task in remaining: task.cancel()
        await asyncio.gather(*remaining,return_exceptions=True)
        event('stopped',activeConnectionsAtCleanup=len(remaining))

if __name__=='__main__':
    parser=argparse.ArgumentParser()
    for name in ('run-id','fixture-id','upstream-host','ready','events'): parser.add_argument('--'+name,required=True)
    parser.add_argument('--upstream-port',required=True,type=int)
    parser.add_argument('--connect-timeout-seconds',type=float,default=5)
    asyncio.run(main(parser.parse_args()))
import { API_BASE } from '@/api/client';

export interface MockRequest {
  method: string;
  path: string;
  query: URLSearchParams;
  body: unknown;
}

export interface MockReply {
  status?: number;
  body?: unknown;
  delayMs?: number;
}

export interface MockRoute {
  method?: string;
  path: string | RegExp;
  reply: (request: MockRequest) => MockReply | Promise<MockReply>;
}

export const neverResolves = (): Promise<never> => new Promise(() => undefined);

const NULL_BODY_STATUSES = new Set([101, 204, 205, 304]);

function matches(route: MockRoute, request: MockRequest): boolean {
  if (route.method && route.method !== request.method) return false;
  return typeof route.path === 'string' ? route.path === request.path : route.path.test(request.path);
}

function readBody(init?: RequestInit): unknown {
  if (typeof init?.body !== 'string') return undefined;
  try {
    return JSON.parse(init.body);
  } catch {
    return init.body;
  }
}

function respond(reply: MockReply): Response {
  const status = reply.status ?? 200;
  return new Response(NULL_BODY_STATUSES.has(status) ? null : JSON.stringify(reply.body ?? null), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function abortable(signal: AbortSignal | null | undefined, work: Promise<Response>): Promise<Response> {
  if (!signal) return work;
  return new Promise((resolve, reject) => {
    const abort = () => reject(new DOMException('The operation was aborted.', 'AbortError'));
    if (signal.aborted) return abort();
    signal.addEventListener('abort', abort, { once: true });
    const release = () => signal.removeEventListener('abort', abort);
    work.then(
      (response) => { release(); resolve(response); },
      (error: unknown) => { release(); reject(error); },
    );
  });
}

export function mockApi(routes: MockRoute[]): () => void {
  const original = globalThis.fetch;
  const base = new URL(API_BASE, window.location.href);
  const basePath = base.pathname.replace(/\/$/, '');
  const isApiRequest = (url: URL) =>
    url.origin === base.origin && (url.pathname === basePath || url.pathname.startsWith(`${basePath}/`));
  globalThis.fetch = (input, init) => {
    const url = new URL(input instanceof Request ? input.url : String(input), window.location.href);
    if (!isApiRequest(url)) return original(input, init);
    const request: MockRequest = {
      method: (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase(),
      path: url.pathname.slice(basePath.length),
      query: url.searchParams,
      body: readBody(init),
    };
    const route = routes.find((candidate) => matches(candidate, request));
    const work = (async () => {
      const reply: MockReply = route
        ? await route.reply(request)
        : { status: 404, body: { error: `No mock registered for ${request.method} ${request.path}` } };
      if (reply.delayMs) await new Promise((resolve) => setTimeout(resolve, reply.delayMs));
      return respond(reply);
    })();
    return abortable(init?.signal, work);
  };
  return () => { globalThis.fetch = original; };
}

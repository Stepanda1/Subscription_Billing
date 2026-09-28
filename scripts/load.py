"""Reproducible write benchmark using only Python 3's standard library."""
import argparse
import concurrent.futures
import json
import math
import platform
import statistics
import time
import urllib.error
import urllib.request
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--url', default='http://127.0.0.1:8080')
parser.add_argument('--requests', type=int, default=200)
parser.add_argument('--concurrency', type=int, default=20)
args = parser.parse_args()
if args.requests < 1 or args.concurrency < 1:
    parser.error('requests and concurrency must be positive')

def request(path, body=None, key=None):
    headers = {'Content-Type': 'application/json'}
    if key:
        headers['Idempotency-Key'] = key
    req = urllib.request.Request(args.url + path,
        data=json.dumps(body).encode() if body is not None else None, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, None

status, user = request('/api/users', {'name': 'Load test', 'email': f'load-{uuid.uuid4()}@example.com'})
if status != 201:
    raise SystemExit(f'Cannot create user: HTTP {status}')

def write(_):
    start = time.perf_counter()
    status, result = request('/api/subscriptions/create',
        {'userId': user['id'], 'plan': 'basic'}, str(uuid.uuid4()))
    return status, (time.perf_counter() - start) * 1000, result

start = time.perf_counter()
with concurrent.futures.ThreadPoolExecutor(max_workers=args.concurrency) as pool:
    results = list(pool.map(write, range(args.requests)))
elapsed = time.perf_counter() - start
latencies = sorted(result[1] for result in results)
accepted = [result[2]['id'] for result in results if result[0] == 202]
codes = {str(code): sum(r[0] == code for r in results) for code in sorted({r[0] for r in results})}

# Poll the bounded list instead of issuing a GET per write.
deadline = time.monotonic() + 90
activated = set()
while time.monotonic() < deadline and len(activated) < len(accepted):
    after = 0
    current = set()
    while True:
        status, page = request(f'/api/subscriptions?userId={user["id"]}&afterId={after}&limit=100')
        if status != 200:
            raise SystemExit(f'Polling failed: HTTP {status}')
        current.update(item['id'] for item in page if item['isActive'])
        if len(page) < 100:
            break
        after = page[-1]['id']
    activated = current
    if len(activated) < len(accepted):
        time.sleep(0.3)

summary = {
    'platform': platform.platform(), 'requests': args.requests,
    'concurrency': args.concurrency, 'elapsed_seconds': round(elapsed, 3),
    'accepted_per_second': round(len(accepted) / elapsed, 2),
    'http_codes': codes,
    'p50_ms': round(statistics.median(latencies), 2),
    'p95_ms': round(latencies[math.ceil(len(latencies) * .95) - 1], 2),
    'accepted': len(accepted), 'activated': len(activated),
    'all_accepted_activated': set(accepted) <= activated,
}
print(json.dumps(summary, indent=2))
if len(accepted) != args.requests or not summary['all_accepted_activated']:
    raise SystemExit(1)

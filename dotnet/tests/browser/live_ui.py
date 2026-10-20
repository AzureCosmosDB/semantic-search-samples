"""Legacy three-row synthetic fixture browser checks, NOT the default MovieLens matrix.

Starts/stops only its own .NET process; never provisions or loads data.
For MovieLens use python/scripts/verify_browser.py --include-dotnet.
"""
import os
import signal
import math
from pathlib import Path
import socket
import subprocess
import time
import urllib.request

from playwright.sync_api import sync_playwright


def main():
    if os.environ.get("RUN_LIVE_TESTS") != "1":
        raise SystemExit("Set RUN_LIVE_TESTS=1 explicitly. This script calls real Azure services.")
    for name in ("COSMOS_DB_ENDPOINT", "COSMOS_DB_DATABASE", "INFERENCE_ENDPOINT"):
        if not os.environ.get(name):
            raise SystemExit(f"Enabled live UI test requires {name}.")
    if os.environ.get("COSMOS_DB_CONTAINER_QFLAT", "reranker_dotnet_qflat") != "reranker_dotnet_qflat":
        raise SystemExit("Live UI test only allows the dedicated .NET qflat container.")
    if os.environ.get("COSMOS_DB_CONTAINER_DISKANN", "reranker_dotnet_diskann") != "reranker_dotnet_diskann":
        raise SystemExit("Live UI test only allows the dedicated .NET diskann container.")
    root = Path(__file__).resolve().parents[3]
    private = root / ".local" / "validation"
    private.mkdir(parents=True, exist_ok=True)
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    url = f"http://127.0.0.1:{port}"
    env = os.environ.copy()
    # Deterministic error-path assertion, independent of any parent OpenAI settings.
    env.pop("AZURE_OPENAI_ENDPOINT", None)
    env.pop("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", None)
    env["ASPNETCORE_ENVIRONMENT"] = "Production"
    with (private / "dotnet-web.log").open("w", encoding="utf-8") as log:
        process = subprocess.Popen(
            ["dotnet", "run", "--no-build", "--no-launch-profile", "--project",
             str(root / "dotnet/src/SemanticReranker.Web"), "--urls", url],
            cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT,
            start_new_session=os.name != "nt",
        )
        try:
            for _ in range(120):
                if process.poll() is not None:
                    raise AssertionError("Web process exited; inspect .local/validation/dotnet-web.log.")
                try:
                    with urllib.request.urlopen(url + "/healthz", timeout=1) as response:
                        if response.status == 200:
                            break
                except (OSError, TimeoutError):
                    time.sleep(0.5)
            else:
                raise AssertionError("Web health endpoint did not become ready.")
            with sync_playwright() as playwright:
                browser = playwright.chromium.launch()
                page = browser.new_page()
                page.set_default_timeout(150_000)
                for container in ("qflat", "diskann"):
                    for rerank in (False, True):
                        page.goto(url)
                        page.get_by_role("textbox", name="Search input", exact=True).fill("ocean marine")
                        page.locator("#Container").select_option(container)
                        page.get_by_role("radio", name="all keywords", exact=True).check()
                        page.locator("#Rerank").set_checked(rerank)
                        page.get_by_role("button", name="Full text search", exact=True).click()
                        page.wait_for_load_state("networkidle")
                        assert page.locator("#search-error").count() == 0
                        assert page.locator("#results tbody tr").count() > 0
                        assert "synthetic-dotnet-ocean" in page.locator("#results").inner_text()
                        assert page.locator("#result-status").inner_text() == (
                            "Reranked results" if rerank else "Retrieval results"
                        )
                        score = page.locator(".rerank-score").first.inner_text()
                        if rerank:
                            assert math.isfinite(float(score))
                        else:
                            assert score == "n/a"
                        assert "RU" in page.locator("#diagnostics").inner_text()
                        page.screenshot(path=str(private / f"dotnet-{container}-rerank-{rerank}.png"))
                for button in ("Similarity search", "Hybrid search"):
                    page.goto(url)
                    page.get_by_role("textbox", name="Search input", exact=True).fill("ocean")
                    page.get_by_role("button", name=button, exact=True).click()
                    page.wait_for_load_state("networkidle")
                    assert "AZURE_OPENAI_ENDPOINT" in page.locator("#search-error").inner_text()
                    assert page.locator("#results").count() == 0
                browser.close()
            print("PASS: HTTP health, real text retrieval and reranking on/off in both containers, visible vector/hybrid missing-configuration errors.")
            print("NOT TESTED: real OpenAI embedding quality or vector/hybrid browser success.")
        finally:
            # dotnet run can own a child process. Terminate this process tree only.
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], check=False, capture_output=True)
            elif process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                if os.name != "nt":
                    os.killpg(process.pid, signal.SIGKILL)
                else:
                    process.kill()
                process.wait()


if __name__ == "__main__":
    main()

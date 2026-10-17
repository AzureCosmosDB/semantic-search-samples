// Presentation only. SearchRequest validation remains authoritative on the server.
(() => {
  const rerank = document.getElementById("Rerank");
  const format = document.getElementById("DocumentFormat");
  const fields = document.getElementById("TargetPaths");
  const options = document.getElementById("rerank-options");
  function update() {
    options.disabled = !rerank.checked;
    fields.disabled = !rerank.checked || format.value !== "json";
  }
  rerank.addEventListener("change", update);
  format.addEventListener("change", update);
  update();
})();

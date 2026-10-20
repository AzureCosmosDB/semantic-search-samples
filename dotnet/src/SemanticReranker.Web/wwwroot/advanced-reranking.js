// Presentation only. SearchRequest validation remains authoritative on the server.
(() => {
  const rerank = document.getElementById("Rerank");
  const format = document.getElementById("DocumentFormat");
  const fields = document.getElementById("TargetPaths");
  const options = document.getElementById("rerank-options");
  const form = document.getElementById("search-form");
  const container = document.getElementById("Container");
  const selectedContainer = document.getElementById("selected-container");
  // The option carries only its configured name, not dataset metadata or an endpoint.
  if (container && selectedContainer) {
    container.addEventListener("change", () => {
      selectedContainer.textContent =
        container.selectedOptions[0]?.dataset.containerName ?? "Select a valid index";
    });
  }
  function update() {
    options.disabled = !rerank.checked;
    fields.disabled = !rerank.checked || format.value !== "json";
  }
  rerank.addEventListener("change", update);
  format.addEventListener("change", update);
  // Disabled controls are otherwise omitted by native submission. Retain their
  // choices across a search with reranking off or text format selected. This
  // never sets Mode: only the actual submit button may choose the search.
  form.addEventListener("formdata", (event) => {
    event.formData.set("DocumentFormat", format.value);
    event.formData.set("TargetPaths", fields.value);
    event.formData.set("RerankTopK", document.getElementById("RerankTopK").value);
    event.formData.set("ReturnSentenceScores",
      String(document.getElementById("ReturnSentenceScores").checked));
  });
  update();
})();

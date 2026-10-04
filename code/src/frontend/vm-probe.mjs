import * as echarts from "echarts";
const run = (label, extra) => {
  const chart = echarts.init(null, null, { renderer: "svg", ssr: true, width: 400, height: 300 });
  try {
    chart.setOption({
      xAxis: { type: "category", data: ["a", "b", "c"] }, yAxis: { type: "value" },
      series: [{ type: "line", data: [0, 0, 1807], color: "#3b82f6" }, { type: "line", data: [0, 0, 0] }],
      visualMap: { show: false, seriesIndex: 0, dimension: 1, pieces: [{ lt: 0, color: "red" }, { gte: 0, color: "green" }] , ...extra },
    });
    console.log(label, "ok");
  } catch (e) { console.log(label, "ERR", e.message); }
};
run("pieces", {});
run("with type piecewise", { type: "piecewise" });
run("continuous-ish", { type: "piecewise", pieces: [{ lt: 0, color: "red" }, { gte: 0, color: "green" }], outOfRange: { color: "#999" } });

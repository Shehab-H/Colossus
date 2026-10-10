export const Section = ({ title, children }: { title: string; children: React.ReactNode }) => (
  <section className="perf-dashboard__section">
    <h3>{title}</h3>
    {children}
  </section>
);

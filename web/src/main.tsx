import { StrictMode, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import "./styles.css";

type SeatState = "AVAILABLE" | "HELD" | "SOLD";
type Seat = { id: string; label: string; kind: "Window" | "Aisle"; state: SeatState };
type Carriage = { id: string; name: string; className: string; fare: number; seats: Seat[] };

const carriages: Carriage[] = [
  { id: "C3", name: "Carriage 3", className: "Soft seat", fare: 1_240_000, seats: [
    { id: "1A", label: "1A", kind: "Window", state: "AVAILABLE" }, { id: "1B", label: "1B", kind: "Aisle", state: "AVAILABLE" }, { id: "1C", label: "1C", kind: "Aisle", state: "HELD" }, { id: "1D", label: "1D", kind: "Window", state: "SOLD" },
    { id: "2A", label: "2A", kind: "Window", state: "AVAILABLE" }, { id: "2B", label: "2B", kind: "Aisle", state: "SOLD" }, { id: "2C", label: "2C", kind: "Aisle", state: "AVAILABLE" }, { id: "2D", label: "2D", kind: "Window", state: "AVAILABLE" },
    { id: "3A", label: "3A", kind: "Window", state: "HELD" }, { id: "3B", label: "3B", kind: "Aisle", state: "AVAILABLE" }, { id: "3C", label: "3C", kind: "Aisle", state: "AVAILABLE" }, { id: "3D", label: "3D", kind: "Window", state: "AVAILABLE" },
  ] },
  { id: "C4", name: "Carriage 4", className: "Sleeper berth", fare: 1_680_000, seats: [
    { id: "1A", label: "1A", kind: "Window", state: "AVAILABLE" }, { id: "1B", label: "1B", kind: "Aisle", state: "AVAILABLE" }, { id: "1C", label: "1C", kind: "Aisle", state: "SOLD" }, { id: "1D", label: "1D", kind: "Window", state: "AVAILABLE" },
    { id: "2A", label: "2A", kind: "Window", state: "SOLD" }, { id: "2B", label: "2B", kind: "Aisle", state: "HELD" }, { id: "2C", label: "2C", kind: "Aisle", state: "AVAILABLE" }, { id: "2D", label: "2D", kind: "Window", state: "AVAILABLE" },
  ] },
];
const money = new Intl.NumberFormat("vi-VN", { style: "currency", currency: "VND", maximumFractionDigits: 0 });

function App() {
  const [from, setFrom] = useState("Hà Nội"); const [to, setTo] = useState("Đà Nẵng"); const [date, setDate] = useState("2027-02-18");
  const [carriageId, setCarriageId] = useState(carriages[0].id); const [selectedSeatIds, setSelectedSeatIds] = useState<string[]>([]);
  const [notice, setNotice] = useState("Select an available seat to continue.");
  const carriage = carriages.find((item) => item.id === carriageId) ?? carriages[0];
  const selectedSeats = useMemo(() => carriage.seats.filter((seat) => selectedSeatIds.includes(seat.id)), [carriage, selectedSeatIds]);
  const chooseCarriage = (id: string) => { setCarriageId(id); setSelectedSeatIds([]); setNotice("Carriage changed. Select an available seat to continue."); };
  const toggleSeat = (seat: Seat) => {
    if (seat.state !== "AVAILABLE") { setNotice(`${seat.label} is ${seat.state.toLowerCase()}. Choose another seat.`); return; }
    if (selectedSeatIds.includes(seat.id)) { setSelectedSeatIds((current) => current.filter((id) => id !== seat.id)); setNotice(`${seat.label} removed from your selection.`); return; }
    if (selectedSeatIds.length === 4) { setNotice("You can select up to four seats in one carriage."); return; }
    setSelectedSeatIds((current) => [...current, seat.id]); setNotice(`${seat.label} added. Availability is confirmed only when a hold is created.`);
  };
  const search = (event: React.FormEvent<HTMLFormElement>) => { event.preventDefault(); setSelectedSeatIds([]); setNotice(`Showing preview journeys from ${from} to ${to} on ${date}.`); };
  return <div className="app-shell">
    <header className="site-header"><a className="logo" href="#top" aria-label="TetRail home">Tet<span>Rail</span></a><nav aria-label="Main navigation"><a className="active" href="#search">Book tickets</a><a href="#journey">My journeys</a><a href="#help">Help</a></nav><button className="account-button" type="button" aria-label="Open account menu"><span>NT</span>Account</button></header>
    <main id="top"><section className="hero" aria-labelledby="page-title"><p className="eyebrow">Tet holiday travel, made simple</p><h1 id="page-title">Find your way home.</h1><p>Search a journey, compare seats, and move to a secure hold when you are ready.</p></section>
      <form id="search" className="search-panel" onSubmit={search}><label>From<input value={from} onChange={(event) => setFrom(event.target.value)} required /></label><label>To<input value={to} onChange={(event) => setTo(event.target.value)} required /></label><label>Departure<input type="date" value={date} onChange={(event) => setDate(event.target.value)} required /></label><button className="primary-button" type="submit">Search journeys</button></form>
      <section id="journey" className="journey-section" aria-labelledby="journey-title"><div className="section-heading"><div><p className="eyebrow">Recommended journey</p><h2 id="journey-title">SE3 · Reunification Express</h2></div><span className="on-time">On time</span></div><div className="route-card"><div><strong>19:25</strong><span>{from}</span></div><div className="route-line"><span>15h 10m · Direct</span></div><div><strong>10:35</strong><span>{to}</span></div><p>Thursday, 18 February 2027 · Standard fare</p></div></section>
      <section className="booking-grid" aria-labelledby="seat-title"><div className="seat-panel"><div className="section-heading compact"><div><p className="eyebrow">Choose a seat</p><h2 id="seat-title">Your carriage</h2></div><span className="updated">Updated 10:24</span></div><div className="carriage-tabs" role="tablist" aria-label="Carriages">{carriages.map((item) => <button key={item.id} className={item.id === carriageId ? "selected" : ""} onClick={() => chooseCarriage(item.id)} type="button" role="tab" aria-selected={item.id === carriageId}>{item.name}<span>{item.className}</span></button>)}</div><div className="seat-layout"><div className="seat-map-copy"><h3>{carriage.name}</h3><p>{carriage.className}. Choose up to four seats in this carriage.</p><div className="legend"><span><i className="available" />Available</span><span><i className="held" />Held</span><span><i className="sold" />Sold</span></div></div><div className="seat-map" aria-label={`${carriage.name} seat map`}>{carriage.seats.map((seat, index) => <div className="seat-cell" key={seat.id}>{index > 0 && index % 4 === 0 && <div className="aisle">Aisle</div>}<button className={`seat ${seat.state.toLowerCase()} ${selectedSeatIds.includes(seat.id) ? "chosen" : ""}`} type="button" onClick={() => toggleSeat(seat)} aria-pressed={selectedSeatIds.includes(seat.id)} aria-label={`${seat.label}, ${seat.kind}, ${seat.state}`}>{seat.label}</button></div>)}</div></div></div>
        <aside className="summary-card" aria-label="Selection summary"><p className="eyebrow">Your selection</p><h2>{selectedSeats.length ? `${selectedSeats.length} ${selectedSeats.length === 1 ? "seat" : "seats"}` : "No seats yet"}</h2><div className="selected-list">{selectedSeats.length ? selectedSeats.map((seat) => <span key={seat.id}>{seat.label} · {seat.kind}</span>) : <span>Choose from the map</span>}</div><dl><div><dt>Passengers</dt><dd>{selectedSeats.length || "—"}</dd></div><div><dt>Fare per seat</dt><dd>{money.format(carriage.fare)}</dd></div><div className="total"><dt>Total</dt><dd>{selectedSeats.length ? money.format(carriage.fare * selectedSeats.length) : "—"}</dd></div></dl><button className="primary-button continue" type="button" disabled={!selectedSeats.length} onClick={() => setNotice("The secure hold step will be connected when the booking API is available.")}>Continue to passenger details</button><p className="availability-note"><strong>Availability is not guaranteed.</strong> Your seats are reserved only after a successful hold is created.</p></aside></section>
      <p className="status-message" aria-live="polite">{notice}</p></main><footer id="help"><span>TetRail · Travel confidently this holiday season.</span><span>Preview interface · API connection pending</span></footer></div>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><App /></StrictMode>);

-- Наш модуль для winws2. К движку его кладут build.cmd и pack.cmd под именем
-- lua/nz-video.lua, пресет подключает строкой --lua-init=@lua/nz-video.lua
-- (Universal V13 YT FIX).
--
-- Первый пакет ответа сервера — не больше двух байт, дальше окно настоящее.
--
-- Фильтр по адресу на зарубежных узлах видео Google (ТСПУ, с октября 2026) режет соединение
-- по первому пакету ответа сервера: начинается он с 16 03 03 (запись TLS) — поток гибнет,
-- какое бы имя ни стояло в приветствии. Смотрит он только этот первый пакет. Замер 10.11 на
-- трёх сетях: окно приёма 2 Б — ответ проходит (3 из 3), 3 Б и больше — нет (0 из 9); после
-- первых двух байт окно можно открыть, и весь ответ сервера (4,6 КБ) приходит за 0,13 с.
--
-- Окно в два байта множителем не выразить: множитель окна задаётся в SYN один раз на всё
-- соединение, у Windows он 8, и наименьшее ненулевое окно — 256 Б. Занизить множитель в SYN
-- (как wssize из zapret-antidpi) — значит до конца соединения показывать серверу окно
-- в 2^(8-заниженный) раз меньше настоящего: скорость падает в десятки раз. Поэтому множитель
-- из SYN убирается вовсе: сервер тогда свой тоже не шлёт, Windows выключает масштаб у себя,
-- и обе стороны считают окно одинаково — до 65535 Б, без подгонки каждого подтверждения
-- (её делает wssize_ramp из custom_funcs, но ей нужны все пустые подтверждения, а их
-- WinDivert отдаёт только с --wf-tcp-empty=1).
--
-- Два шага, потому что у SYN имени ещё нет и его берёт секция по адресам, а приветствие —
-- секция по имени:
--   nz_first2:syn  — в секции по адресам: убирает множитель окна из SYN и помечает соединение;
--   nz_first2      — в секции по имени, перед рецептом приветствия: пока сервер не прислал
--                    ни байта, ставит исходящим пакетам окно в два байта. Соединения без
--                    пометки не трогает — у узла провайдера множитель на месте, и «два»
--                    значило бы 512 Б и лишнее ожидание.
-- Подтверждение первых двух байт уходит от Windows уже с настоящим окном — пустые пакеты
-- сюда не попадают, — и сервер шлёт остальное как обычно.
--
-- standard args : direction
-- arg : syn - шаг для SYN (см. выше)
-- arg : wsize - окно до первых байт ответа сервера (по умолчанию 2)
function nz_first2(ctx, desync)
	local dis = desync.dis
	if not dis.tcp then
		if not dis.icmp then instance_cutoff_shim(ctx, desync) end
		return
	end
	direction_cutoff_opposite(ctx, desync)
	if not direction_check(desync) then return end
	local track = desync.track
	if not track then
		-- Без учёта соединения не узнать ни пометки, ни того, начал ли сервер отвечать.
		instance_cutoff_shim(ctx, desync)
		return
	end
	local tcp = dis.tcp
	local st = track.lua_state
	if desync.arg.syn then
		if bitand(tcp.th_flags, TH_SYN) == 0 then
			instance_cutoff_shim(ctx, desync)
			return
		end
		local i = find_tcp_option(tcp.options, TCP_KIND_SCALE)
		st.nz_first2 = true
		if not i then return end
		table.remove(tcp.options, i)
		if b_debug then DLOG("nz_first2: SYN без множителя окна") end
		return VERDICT_MODIFY
	end
	if not st.nz_first2 then
		instance_cutoff_shim(ctx, desync)
		return
	end
	local server = track.pos and track.pos.server and track.pos.server.tcp
	local acked = 0
	if server and server.seq0 then
		acked = (tcp.th_ack - server.seq0 - 1) % 4294967296
		if acked >= 2147483648 then acked = 0 end
	end
	if acked > 0 then
		instance_cutoff_shim(ctx, desync)
		return
	end
	tcp.th_win = tonumber(desync.arg.wsize) or 2
	if b_debug then DLOG("nz_first2: окно "..tcp.th_win.." до первых байт ответа") end
	return VERDICT_MODIFY
end

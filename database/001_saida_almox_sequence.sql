BEGIN;
CREATE SEQUENCE IF NOT EXISTS almoxarifado_jac.t_saida_almox_movimentacao_seq;
SELECT setval(
    'almoxarifado_jac.t_saida_almox_movimentacao_seq',
    COALESCE((SELECT MAX(cod_movimentacao) FROM almoxarifado_jac.t_saida_almox), 0) + 1,
    false
);
COMMIT;
